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
        private bool _prevStableIsTransmitting = false;
        private const int TxOffDebounceCount = 2;

        // Antenna sync: the radio doesn't broadcast AN auto-information messages when the
        // operator changes the antenna on the front panel, so we have to poll. Poll when
        // a wall-clock deadline elapses (~2 s) rather than counting cycles, so the interval
        // is independent of MeterPollIntervalMs.
        private DateTime _lastAntennaPollUtc = DateTime.MinValue;
        private static readonly TimeSpan AntennaPollInterval = TimeSpan.FromMilliseconds(2000);

        // Frequency backstop poll — single-receiver radios only. On the FTdx10 /
        // FT-710 / FTDX3000 the radio's unsolicited FA/FB auto-info pushes are
        // unreliable, especially over shared-CAT / VSPE setups (iu1teu #78), so
        // the frequency display can go stale with no way to recover. Poll FA;/FB;
        // ~once a second as a fallback — the same approach iu1teu's own bridge
        // uses on the same radio. The dual-receiver FTdx101, where auto-info
        // works, is deliberately left event-driven.
        // Suppressed briefly after a user web-UI write so the read-back can't
        // fight active tuning (see RadioStateService.LastUserFrequencyWriteUtc).
        private DateTime _lastFreqPollUtc = DateTime.MinValue;
        private static readonly TimeSpan FreqPollInterval = TimeSpan.FromMilliseconds(1000);
        private static readonly TimeSpan FreqPollWriteSuppress = TimeSpan.FromMilliseconds(1200);

        // Connection health: track wall-clock time of the first consecutive null poll response
        // to detect radio power-off. The serial port stays "open" on Windows when the radio
        // powers off; null responses (timeouts) are the only signal. After ~2.5 s of consecutive
        // nulls we broadcast disconnected — cadence-independent.
        private DateTime? _firstNullUtc = null;
        private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromMilliseconds(2500);

        // S-meter zero-flash debounce. Yaesu radios occasionally respond to
        // SM0; with a transient zero — typically when the radio is busy
        // emitting FA auto-info during a frequency-dial turn — even though
        // the actual signal is unchanged. Without debouncing, the S-meter
        // needle briefly drops to S0 then snaps back, producing the visible
        // "flash" Jacek SP3L reported on #34 pre6.
        //
        // Record the timestamp of the first zero; only propagate zero after
        // the hold duration has elapsed. Non-zero readings propagate immediately
        // (so the meter remains snappy when signal is actually present).
        private DateTime? _sMeterZeroSinceUtc = null;
        private static readonly TimeSpan SMeterZeroHold = TimeSpan.FromMilliseconds(1000);

        // Same zero-flash debounce as SMeterA, applied independently to
        // SMeterB (SUB receiver) so a transient zero on one VFO's meter
        // doesn't hold the other's needle back.
        private DateTime? _sMeterBZeroSinceUtc = null;

        // --- FTdx101 meter borrowing ------------------------------------------
        //
        // Reading compression + SWR on the FTdx101 needs the radio's two front-
        // panel meter slots pinned to MS13 so RM0 returns the pair (see the long
        // comment at the RM0 branch below). Until now that MS13 went out on every
        // 500 ms cycle, which meant the operator could never change the meters on
        // the touchscreen — the poller stomped their choice twice a second, and it
        // was the only thing Fabio's video stream could ever show.
        //
        // Both borrowed values are TX-only: SWRMeter and CompressionMeter are
        // forced to 0 whenever the radio isn't transmitting, so during receive the
        // poller was pinning the display to harvest readings it then threw away.
        // So: borrow the meters when TX starts, hand them straight back once TX has
        // been quiet for MeterReturnDelay. During receive the radio shows whatever
        // the operator picked.
        //
        // The return delay is deliberately longer than a between-overs gap. Handing
        // the meters back the instant TX drops would make the display flap once per
        // over on SSB and strobe on QSK CW break-in, which is worse than leaving it
        // pinned. 10 s means a normal QSO keeps the meters borrowed for its
        // duration and they come back during genuine idle/listening time — which is
        // when a video stream of the front panel is worth watching anyway.
        private DateTime _lastTxSeenUtc = DateTime.MinValue;
        private bool _metersBorrowed = false;
        private static readonly TimeSpan MeterReturnDelay = TimeSpan.FromSeconds(10);

        // Wall-clock settle window after MS13. The radio needs a moment to settle
        // onto the newly-selected meters, so the first RM0 read after an MS13 write
        // can carry a value belonging to the previous selection. Discard readings
        // until this deadline passes. If bench testing shows SWR coming up a beat
        // late, this is the knob.
        private DateTime _borrowSettleUntilUtc = DateTime.MinValue;
        private static readonly TimeSpan BorrowSettleWindow = TimeSpan.FromMilliseconds(500);

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
                int intervalMs = 200;
                try
                {
                    var settings = await _settingsService.GetSettingsAsync();
                    intervalMs = Math.Clamp(settings.MeterPollIntervalMs, 50, 1000);

                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    // ── Fast tier: every cycle ─────────────────────────────────────────
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
                            if (_firstNullUtc == null)
                                _firstNullUtc = DateTime.UtcNow;
                            else if (DateTime.UtcNow - _firstNullUtc >= DisconnectTimeout)
                                _stateService.IsConnected = false;
                        }
                    }
                    else
                    {
                        _firstNullUtc = null;
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

                    // TX-off transition: zero all TX-only meters immediately so clients
                    // never see a stale peak from the last debounce cycle.
                    if (!isTransmitting && _prevStableIsTransmitting)
                    {
                        _stateService.CompressionMeter = 0;
                        _stateService.ALCMeter         = 0;
                        _stateService.IDDMeter         = 0;
                        _stateService.SWRMeter         = 0;
                        if (_stateService.PowerMeter != 0) _stateService.PowerMeter = 0;
                    }
                    _prevStableIsTransmitting = isTransmitting;

                    _logger.LogDebug("[MeterPolling][DEBUG] Polling S-Meter A...");
                    var smAResponse = await _multiplexer.SendCommandAsync(CatCommands.SMeterMain + ";", "MeterPoll", stoppingToken);
                    _logger.LogDebug("[MeterPolling][DEBUG] S-Meter A response: {0}", smAResponse);
                    int sMeterA = CatCommands.ParseSMeter(smAResponse ?? "");

                    // Zero-flash debounce (see _sMeterZeroSinceUtc comment). Apply
                    // a non-zero reading immediately, but only propagate a zero
                    // after the hold duration has elapsed.
                    if (sMeterA == 0)
                    {
                        if (_sMeterZeroSinceUtc == null)
                            _sMeterZeroSinceUtc = DateTime.UtcNow;
                        else if (DateTime.UtcNow - _sMeterZeroSinceUtc >= SMeterZeroHold)
                            _stateService.SMeterA = 0;
                        // else: hold the previous SMeterA value, don't overwrite.
                    }
                    else
                    {
                        _sMeterZeroSinceUtc = null;
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
                            if (_sMeterBZeroSinceUtc == null)
                                _sMeterBZeroSinceUtc = DateTime.UtcNow;
                            else if (DateTime.UtcNow - _sMeterBZeroSinceUtc >= SMeterZeroHold)
                                _stateService.SMeterB = 0;
                            // else: hold the previous SMeterB value, don't overwrite.
                        }
                        else
                        {
                            _sMeterBZeroSinceUtc = null;
                            _stateService.SMeterB = sMeterB;
                        }
                    }

                    // ── TX tier: only while transmitting ──────────────────────────────
                    if (isTransmitting)
                    {
                        _logger.LogDebug("[MeterPolling][DEBUG] Polling Power Meter...");
                        var powerResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterPower + ";", "MeterPoll", stoppingToken);
                        _logger.LogDebug("[MeterPolling][DEBUG] Power Meter response: {0}", powerResponse);
                        int power = CatCommands.ParseMeterReading(powerResponse ?? "");
                        _stateService.PowerMeter = power;
                        _logger.LogDebug("[MeterPolling] Power meter (TX): raw='{Raw}', value={Value}", powerResponse, power);

                        _logger.LogDebug("[MeterPolling][DEBUG] Polling ALC Meter...");
                        var alcResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterALC + ";", "MeterPoll", stoppingToken);
                        _logger.LogDebug("[MeterPolling][DEBUG] ALC Meter response: {0}", alcResponse);
                        int alc = CatCommands.ParseMeterReading(alcResponse ?? "");
                        _stateService.ALCMeter = alc;

                        _logger.LogDebug("[MeterPolling][DEBUG] Polling IDD Meter...");
                        var iddResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterIDD + ";", "MeterPoll", stoppingToken);
                        _logger.LogDebug("[MeterPolling][DEBUG] IDD Meter response: {0}", iddResponse);
                        int idd = CatCommands.ParseMeterReading(iddResponse ?? "");
                        _stateService.IDDMeter = idd;

                        // SWR and Compression meter polling differs by radio model.
                        //
                        //  - FTdx101MP / FTdx101D: RM6 (the documented SWR read) returns
                        //    stale/wrong values, so we have to set both meter slots via
                        //    MS13 (compression-left, SWR-right) and then read both at once
                        //    with RM0. Pre-existing workaround, do not regress. The MS13
                        //    is now issued only while transmitting and handed back
                        //    afterwards — see the meter-borrowing comment above.
                        //
                        //  - FTdx10 / FT-710 / FTDX3000: MS13+RM0 doesn't read SWR
                        //    correctly (reported by OE5HMR on FTdx10, 2026-06-01).
                        //    Reverting to the documented direct reads — RM3 for
                        //    compression, RM6 for SWR — which is what Yaesu's CAT
                        //    manual specifies for these radios. These models never touch
                        //    MS at all, so their front-panel meters were always free and
                        //    nothing in the borrowing logic applies to them.
                        bool useRm0Pair = settings.RadioModel is "FTdx101MP" or "FTdx101D";
                        int compression;
                        int swr;
                        if (useRm0Pair)
                        {
                            _lastTxSeenUtc = DateTime.UtcNow;
                            if (!_metersBorrowed)
                            {
                                // Flag before the write: the radio echoes MS via auto-
                                // information, and the dispatcher must not mistake our
                                // own MS13 for an operator choice.
                                _stateService.MetersBorrowed = true;
                                _metersBorrowed = true;
                                _borrowSettleUntilUtc = DateTime.UtcNow.Add(BorrowSettleWindow);
                                _logger.LogDebug("[MeterPolling] TX started — borrowing front-panel meters (MS13)");
                                await _multiplexer.SendCommandAsync(CatCommands.SetMetersCompAndSWR + ";", "MeterPoll", stoppingToken);
                            }

                            var compSwrResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterBoth + ";", "MeterPoll", stoppingToken);
                            _logger.LogDebug("[MeterPolling][DEBUG] RM0 response: '{Raw}'", compSwrResponse);
                            compression = CatCommands.ParseRm0LeftMeter(compSwrResponse ?? "");
                            swr         = CatCommands.ParseRm0RightMeter(compSwrResponse ?? "");

                            if (DateTime.UtcNow < _borrowSettleUntilUtc)
                            {
                                // Meters have only just switched — this read may still
                                // describe the operator's previous selection.
                                compression = 0;
                                swr         = 0;
                            }
                        }
                        else
                        {
                            _logger.LogDebug("[MeterPolling][DEBUG] Polling Compression (RM3) and SWR (RM6) directly...");
                            var compResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterComp + ";", "MeterPoll", stoppingToken);
                            _logger.LogDebug("[MeterPolling][DEBUG] RM3 response: '{Raw}'", compResponse);
                            compression = CatCommands.ParseMeterReading(compResponse ?? "");
                            var swrResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterSWR + ";", "MeterPoll", stoppingToken);
                            _logger.LogDebug("[MeterPolling][DEBUG] RM6 response: '{Raw}'", swrResponse);
                            swr = CatCommands.ParseMeterReading(swrResponse ?? "");
                        }

                        _stateService.SWRMeter = swr;
                        _stateService.CompressionMeter = compression;
                        _logger.LogDebug("[MeterPolling][DEBUG] TX Compression={Comp} SWR={SWR}", compression, swr);
                    }
                    else
                    {
                        // ── Software zeroing during RX ─────────────────────────────────
                        if (_stateService.PowerMeter != 0)
                            _stateService.PowerMeter = 0;
                        if (_stateService.ALCMeter != 0)
                            _stateService.ALCMeter = 0;
                        if (_stateService.CompressionMeter != 0)
                            _stateService.CompressionMeter = 0;
                        _stateService.SWRMeter = 0;

                        // Return borrowed meters once TX has been quiet for MeterReturnDelay.
                        bool useRm0Pair = settings.RadioModel is "FTdx101MP" or "FTdx101D";
                        if (useRm0Pair && _metersBorrowed && DateTime.UtcNow - _lastTxSeenUtc > MeterReturnDelay)
                        {
                            var restore = _stateService.RadioMeterSelection
                                          ?? CatCommands.DefaultFtdx101MeterSelection;
                            _logger.LogInformation("[MeterPolling] TX idle — returning front-panel meters to MS{Restore}", restore);
                            await _multiplexer.SendCommandAsync($"MS{restore};", "MeterPoll", stoppingToken);
                            _metersBorrowed = false;
                            // Cleared last: any MS echo of the restore carries the
                            // operator's own value, so recording it is harmless.
                            _stateService.MetersBorrowed = false;
                        }
                    }

                    // ── Slow tier: wall-clock deadlines ───────────────────────────────
                    var now = DateTime.UtcNow;

                    if (now - _lastAntennaPollUtc >= AntennaPollInterval)
                    {
                        _lastAntennaPollUtc = now;
                        _logger.LogDebug("[MeterPolling][DEBUG] Polling VDD, Temperature, Antenna...");
                        var vddResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterVDD + ";", "MeterPoll", stoppingToken);
                        int vdd = CatCommands.ParseMeterReading(vddResponse ?? "");
                        _stateService.VDDMeter = vdd;

                        var tempResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterTemp + ";", "MeterPoll", stoppingToken);
                        int temp = CatCommands.ParseMeterReading(tempResponse ?? "");
                        _stateService.Temperature = temp;

                        var an0 = await _multiplexer.SendCommandAsync("AN0;", "MeterPoll", stoppingToken);
                        if (!string.IsNullOrEmpty(an0)) _dispatcher.DispatchMessage(an0);
                        var an1 = await _multiplexer.SendCommandAsync("AN1;", "MeterPoll", stoppingToken);
                        if (!string.IsNullOrEmpty(an1)) _dispatcher.DispatchMessage(an1);
                    }

                    // Frequency backstop poll — single-receiver radios only (see field comment).
                    if (_stateService.IsSingleReceiver && now - _lastFreqPollUtc >= FreqPollInterval)
                    {
                        _lastFreqPollUtc = now;
                        // Skip while the user is actively tuning from the web UI, so a
                        // read-back can't briefly show a stale value mid-write.
                        if (now - _stateService.LastUserFrequencyWriteUtc > FreqPollWriteSuppress)
                        {
                            var fa = await _multiplexer.SendCommandAsync("FA;", "MeterPoll", stoppingToken);
                            if (!string.IsNullOrEmpty(fa)) _dispatcher.DispatchMessage(fa);
                            var fb = await _multiplexer.SendCommandAsync("FB;", "MeterPoll", stoppingToken);
                            if (!string.IsNullOrEmpty(fb)) _dispatcher.DispatchMessage(fb);
                        }
                    }

                    sw.Stop();
                    var elapsed = (int)sw.ElapsedMilliseconds;
                    var delay = Math.Max(0, intervalMs - elapsed);
                    _logger.LogDebug("[MeterPolling] Cycle elapsed: {ElapsedMs} ms, delay: {DelayMs} ms", elapsed, delay);

                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[MeterPolling][FATAL] Exception in polling loop: {Message}\nStackTrace: {StackTrace}", ex.Message, ex.StackTrace);
                    try { await Task.Delay(intervalMs, stoppingToken); } catch (OperationCanceledException) { break; }
                }
            }
        }

        // FTdx101 power-meter restore on shutdown (discussion #6, F1ubw) lives
        // in RadioInitializationService.StopAsync, not here — the hosted-service
        // reverse-shutdown order means MeterPollingService stops AFTER
        // RadioInitializationService has already disconnected the multiplexer,
        // so sending the MS restore from this StopAsync would talk to a closed port (at
        // best a no-op, at worst a hang that stalls host shutdown).
    }
}
