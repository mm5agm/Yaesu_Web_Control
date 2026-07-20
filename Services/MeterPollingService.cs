using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Background service that polls S-meters, power, and SWR independently of AI mode
    /// </summary>
    public class MeterPollingService : BackgroundService
    {
        private readonly CatMultiplexerService _multiplexer;
        private readonly RadioStateService _stateService;
        private readonly CatMessageDispatcher _dispatcher;
        private readonly ILogger<MeterPollingService> _logger;
        private readonly ISettingsService _settingsService;

        // TX debounce: require 2 consecutive TX0 readings before declaring not-transmitting
        // when we currently believe TX is on. A single TX0 mid-burst would otherwise clear
        // the SWR rolling-average and cause a momentary spike on the next reading.
        // Authoritative TX-off (web button / voice / etc. already cleared IsTransmitting)
        // is accepted on the first confirming TX0 — otherwise this poller would overwrite
        // that false back to true and the UI would stay on TX for several poll cycles.
        private int _txFalseCount = 0;
        private bool _stableIsTransmitting = false;
        private const int TxOffDebounceCount = 2;

        // Antenna sync: the radio doesn't broadcast AN auto-information messages when the
        // operator changes the antenna on the front panel, so we have to poll. Polling on
        // every meter-cycle (2 Hz) would be wasteful for a near-static value, so we poll
        // every Nth cycle (~2 seconds at the current loop cadence). Keeps the YWC antenna
        // selector in sync with the radio without saturating the serial bus.
        private int _antennaPollCounter = 0;
        private const int AntennaPollEvery = 4;

        // Frequency backstop poll — single-receiver radios only. On the FTdx10 /
        // FT-710 / FTDX3000 the radio's unsolicited FA/FB auto-info pushes are
        // unreliable, especially over shared-CAT / VSPE setups (iu1teu #78), so
        // the frequency display can go stale with no way to recover. Poll FA;/FB;
        // ~once a second (every other ~500 ms cycle) as a fallback — the same
        // approach iu1teu's own bridge uses on the same radio. The dual-receiver
        // FTdx101, where auto-info works, is deliberately left event-driven.
        // Suppressed briefly after a user web-UI write so the read-back can't
        // fight active tuning (see RadioStateService.LastUserFrequencyWriteUtc).
        private int _freqPollCounter = 0;
        private const int FreqPollEvery = 2;
        private static readonly TimeSpan FreqPollWriteSuppress = TimeSpan.FromMilliseconds(1200);

        // Connection health: track consecutive null poll responses to detect radio power-off.
        // The serial port stays "open" on Windows when the radio powers off; null responses (timeouts)
        // are the only signal. After 5 consecutive nulls (~2.5 s) we broadcast disconnected.
        private int _consecutiveNullCount = 0;
        private const int DisconnectThreshold = 5;

        // S-meter zero-flash debounce. Yaesu radios occasionally respond to
        // SM0; with a transient zero — typically when the radio is busy
        // emitting FA auto-info during a frequency-dial turn — even though
        // the actual signal is unchanged. Without debouncing, the S-meter
        // needle briefly drops to S0 then snaps back, producing the visible
        // "flash" Jacek SP3L reported on #34 pre6.
        //
        // The debounce: only propagate a zero reading after we've seen N
        // consecutive zero responses. Non-zero readings propagate immediately
        // (so the meter remains snappy when signal is actually present).
        private int _sMeterZeroCount = 0;
        private const int SMeterZeroThreshold = 3;

        // Same zero-flash debounce as SMeterA, applied independently to
        // SMeterB (SUB receiver) so a transient zero on one VFO's meter
        // doesn't hold the other's needle back.
        private int _sMeterBZeroCount = 0;

        public MeterPollingService(
            CatMultiplexerService multiplexer,
            RadioStateService stateService,
            CatMessageDispatcher dispatcher,
            ILogger<MeterPollingService> logger,
            ISettingsService settingsService)
        {
            _multiplexer = multiplexer;
            _stateService = stateService;
            _dispatcher = dispatcher;
            _logger = logger;
            _settingsService = settingsService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Meter polling service started (S-Meter, Power, SWR)");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogDebug("[MeterPolling][DEBUG] Polling TX status...");
                    var txResponse = await _multiplexer.SendCommandAsync("TX;", "MeterPoll", stoppingToken);
                    _logger.LogDebug("[MeterPolling][DEBUG] TX response: {0}", txResponse);

                    // Connection health tracking: only applies once the radio has been successfully
                    // initialised (IsConnected = true). Null responses mean no reply from the radio.
                    // Don't count null responses toward disconnect while transmitting — the radio may
                    // be too busy handling TX to answer CAT queries (hardware PTT in particular).
                    if (txResponse == null)
                    {
                        if (_stateService.IsConnected && !_stableIsTransmitting)
                        {
                            _consecutiveNullCount++;
                            if (_consecutiveNullCount >= DisconnectThreshold)
                                _stateService.IsConnected = false;
                        }
                    }
                    else
                    {
                        _consecutiveNullCount = 0;
                        if (!_stateService.IsConnected)
                            _stateService.IsConnected = true;
                    }

                    // TX1 = keyed; TX2 = keyed (mic) on some Yaesu models — treat both as TX.
                    bool rawIsTransmitting = !string.IsNullOrEmpty(txResponse)
                        && (txResponse.Contains("TX1") || txResponse.Contains("TX2"));
                    if (rawIsTransmitting)
                    {
                        _txFalseCount = 0;
                        _stableIsTransmitting = true;
                    }
                    else if (txResponse != null)
                    {
                        // Explicit TX0 only — null (radio busy) must not count as unkey.
                        _txFalseCount++;
                        if (_txFalseCount >= TxOffDebounceCount || !_stateService.IsTransmitting)
                            _stableIsTransmitting = false;
                    }
                    else if (!_stateService.IsTransmitting)
                    {
                        // Authoritative TX-off already applied; a null poll must not resurrect TX.
                        _stableIsTransmitting = false;
                    }
                    bool isTransmitting = _stableIsTransmitting;
                    _stateService.IsTransmitting = isTransmitting;
                    _logger.LogDebug("[MeterPolling] TX poll: raw='{Raw}', rawTX={RawTX}, stableTX={StableTX}", txResponse, rawIsTransmitting, isTransmitting);

                    _logger.LogDebug("[MeterPolling][DEBUG] Polling S-Meter A...");
                    var smAResponse = await _multiplexer.SendCommandAsync(CatCommands.SMeterMain + ";", "MeterPoll", stoppingToken);
                    _logger.LogDebug("[MeterPolling][DEBUG] S-Meter A response: {0}", smAResponse);
                    int sMeterA = CatCommands.ParseSMeter(smAResponse ?? "");

                    // Zero-flash debounce (see _sMeterZeroCount comment). Apply
                    // a non-zero reading immediately, but only propagate a zero
                    // after SMeterZeroThreshold consecutive zeros.
                    if (sMeterA == 0)
                    {
                        _sMeterZeroCount++;
                        if (_sMeterZeroCount >= SMeterZeroThreshold)
                            _stateService.SMeterA = 0;
                        // else: hold the previous SMeterA value, don't overwrite.
                    }
                    else
                    {
                        _sMeterZeroCount = 0;
                        _stateService.SMeterA = sMeterA;
                    }

                    if (_stateService.IsSingleReceiver)
                    {
                        // No SUB receiver to poll. Mirror SMeterA so any
                        // leftover consumer of SMeterB still reads something
                        // sane rather than a stale default.
                        _stateService.SMeterB = sMeterA;
                    }
                    else
                    {
                        _logger.LogDebug("[MeterPolling][DEBUG] Polling S-Meter B...");
                        var smBResponse = await _multiplexer.SendCommandAsync(CatCommands.SMeterSub + ";", "MeterPoll", stoppingToken);
                        _logger.LogDebug("[MeterPolling][DEBUG] S-Meter B response: {0}", smBResponse);
                        int sMeterB = CatCommands.ParseSMeter(smBResponse ?? "");

                        if (sMeterB == 0)
                        {
                            _sMeterBZeroCount++;
                            if (_sMeterBZeroCount >= SMeterZeroThreshold)
                                _stateService.SMeterB = 0;
                            // else: hold the previous SMeterB value, don't overwrite.
                        }
                        else
                        {
                            _sMeterBZeroCount = 0;
                            _stateService.SMeterB = sMeterB;
                        }
                    }

                    _logger.LogDebug("[MeterPolling][DEBUG] Polling Power Meter...");
                    var powerResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterPower + ";", "MeterPoll", stoppingToken);
                    _logger.LogDebug("[MeterPolling][DEBUG] Power Meter response: {0}", powerResponse);
                    int power = CatCommands.ParseMeterReading(powerResponse ?? "");
                    _logger.LogDebug("[MeterPolling][DEBUG] TX={IsTransmitting} Power raw='{Raw}', parsed={Value}", isTransmitting, powerResponse, power);
                    if (isTransmitting)
                    {
                        _stateService.PowerMeter = power;
                        _logger.LogDebug("[MeterPolling] Power meter (TX): raw='{Raw}', value={Value}", powerResponse, power);
                    }
                    else
                    {
                        // Always broadcast PowerMeter=0 when not transmitting
                        if (_stateService.PowerMeter != 0)
                        {
                            _stateService.PowerMeter = 0;
                            _logger.LogDebug("[MeterPolling] Power meter (not TX): value=0");
                        }
                    }

                    // SWR and Compression meter polling differs by radio model.
                    //
                    //  - FTdx101MP / FTdx101D: RM6 (the documented SWR read) returns
                    //    stale/wrong values, so we have to set both meter slots via
                    //    MS13 (compression-left, SWR-right) and then read both at once
                    //    with RM0. Pre-existing workaround, do not regress.
                    //
                    //  - FTdx10 / FT-710 / FTDX3000: MS13+RM0 doesn't read SWR
                    //    correctly (reported by OE5HMR on FTdx10, 2026-06-01).
                    //    Reverting to the documented direct reads — RM3 for
                    //    compression, RM6 for SWR — which is what Yaesu's CAT
                    //    manual specifies for these radios.
                    var settings = await _settingsService.GetSettingsAsync();
                    bool useRm0Pair = settings.RadioModel is "FTdx101MP" or "FTdx101D";
                    int compression;
                    int swr;
                    if (useRm0Pair)
                    {
                        _logger.LogDebug("[MeterPolling][DEBUG] Polling Compression+SWR (MS13+RM0)... TX={IsTransmitting}", isTransmitting);
                        await _multiplexer.SendCommandAsync(CatCommands.SetMetersCompAndSWR + ";", "MeterPoll", stoppingToken);
                        var compSwrResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterBoth + ";", "MeterPoll", stoppingToken);
                        _logger.LogDebug("[MeterPolling][DEBUG] MS13+RM0 response: '{Raw}'", compSwrResponse);
                        compression = CatCommands.ParseRm0LeftMeter(compSwrResponse ?? "");
                        swr         = CatCommands.ParseRm0RightMeter(compSwrResponse ?? "");
                    }
                    else
                    {
                        _logger.LogDebug("[MeterPolling][DEBUG] Polling Compression (RM3) and SWR (RM6) directly... TX={IsTransmitting}", isTransmitting);
                        var compResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterComp + ";", "MeterPoll", stoppingToken);
                        _logger.LogDebug("[MeterPolling][DEBUG] RM3 response: '{Raw}'", compResponse);
                        compression = CatCommands.ParseMeterReading(compResponse ?? "");
                        var swrResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterSWR + ";", "MeterPoll", stoppingToken);
                        _logger.LogDebug("[MeterPolling][DEBUG] RM6 response: '{Raw}'", swrResponse);
                        swr = CatCommands.ParseMeterReading(swrResponse ?? "");
                    }
                    _logger.LogDebug("[MeterPolling][DEBUG] TX={IsTransmitting} Compression={Comp} SWR={SWR}", isTransmitting, compression, swr);
                    _stateService.SWRMeter = isTransmitting ? swr : 0;
                    if (isTransmitting)
                    {
                        _stateService.CompressionMeter = compression;
                    }
                    else
                    {
                        if (_stateService.CompressionMeter != 0)
                            _stateService.CompressionMeter = 0;
                    }

                    _logger.LogDebug("[MeterPolling][DEBUG] Polling IDD Meter...");
                    var iddResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterIDD + ";", "MeterPoll", stoppingToken);
                    _logger.LogDebug("[MeterPolling][DEBUG] IDD Meter response: {0}", iddResponse);
                    int idd = CatCommands.ParseMeterReading(iddResponse ?? "");
                    _stateService.IDDMeter = idd;

                    _logger.LogDebug("[MeterPolling][DEBUG] Polling ALC Meter...");
                    var alcResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterALC + ";", "MeterPoll", stoppingToken);
                    _logger.LogDebug("[MeterPolling][DEBUG] ALC Meter response: {0}", alcResponse);
                    int alc = CatCommands.ParseMeterReading(alcResponse ?? "");
                    if (isTransmitting)
                    {
                        _stateService.ALCMeter = alc;
                    }
                    else
                    {
                        if (_stateService.ALCMeter != 0)
                            _stateService.ALCMeter = 0;
                    }

                    _logger.LogDebug("[MeterPolling][DEBUG] Polling VDD Meter...");
                    var vddResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterVDD + ";", "MeterPoll", stoppingToken);
                    _logger.LogDebug("[MeterPolling][DEBUG] VDD Meter response: {0}", vddResponse);
                    int vdd = CatCommands.ParseMeterReading(vddResponse ?? "");
                    _stateService.VDDMeter = vdd;

                    _logger.LogDebug("[MeterPolling][DEBUG] Polling Temperature...");
                    var tempResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterTemp + ";", "MeterPoll", stoppingToken);
                    _logger.LogDebug("[MeterPolling][DEBUG] Temperature response: {0}", tempResponse);
                    int temp = CatCommands.ParseMeterReading(tempResponse ?? "");
                    _stateService.Temperature = temp;

                    // Antenna poll (low cadence — see field comment above).
                    if (++_antennaPollCounter >= AntennaPollEvery)
                    {
                        _antennaPollCounter = 0;
                        var an0 = await _multiplexer.SendCommandAsync("AN0;", "MeterPoll", stoppingToken);
                        if (!string.IsNullOrEmpty(an0)) _dispatcher.DispatchMessage(an0);
                        var an1 = await _multiplexer.SendCommandAsync("AN1;", "MeterPoll", stoppingToken);
                        if (!string.IsNullOrEmpty(an1)) _dispatcher.DispatchMessage(an1);
                    }

                    // Frequency backstop poll — single-receiver radios only (see field comment).
                    if (_stateService.IsSingleReceiver && ++_freqPollCounter >= FreqPollEvery)
                    {
                        _freqPollCounter = 0;
                        // Skip while the user is actively tuning from the web UI, so a
                        // read-back can't briefly show a stale value mid-write.
                        if (DateTime.UtcNow - _stateService.LastUserFrequencyWriteUtc > FreqPollWriteSuppress)
                        {
                            var fa = await _multiplexer.SendCommandAsync("FA;", "MeterPoll", stoppingToken);
                            if (!string.IsNullOrEmpty(fa)) _dispatcher.DispatchMessage(fa);
                            var fb = await _multiplexer.SendCommandAsync("FB;", "MeterPoll", stoppingToken);
                            if (!string.IsNullOrEmpty(fb)) _dispatcher.DispatchMessage(fb);
                        }
                    }

                    await Task.Delay(500, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[MeterPolling][FATAL] Exception in polling loop: {Message}\nStackTrace: {StackTrace}", ex.Message, ex.StackTrace);
                    try { await Task.Delay(500, stoppingToken); } catch (OperationCanceledException) { break; }
                }
            }
        }

        // FTdx101 power-meter restore on shutdown (discussion #6, F1ubw) lives
        // in RadioInitializationService.StopAsync, not here — the hosted-service
        // reverse-shutdown order means MeterPollingService stops AFTER
        // RadioInitializationService has already disconnected the multiplexer,
        // so sending MS01 from this StopAsync would talk to a closed port (at
        // best a no-op, at worst a hang that stalls host shutdown).
    }
}
