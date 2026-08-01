using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using Yaesu_Web_Control.Hubs;
using Yaesu_Web_Control.Models;
using System.Diagnostics; // Place at the top of the file if not already present

namespace Yaesu_Web_Control.Services
{
    public class RadioInitializationService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IHubContext<RadioHub> _hubContext;
        private readonly BrowserLauncher _browserLauncher;
        private readonly CatMultiplexerService _multiplexer;
        private readonly HttpPortInfo _portInfo;

        public RadioInitializationService(
            IServiceProvider serviceProvider,
            IHubContext<RadioHub> hubContext,
            BrowserLauncher browserLauncher,
            CatMultiplexerService multiplexer,
            HttpPortInfo portInfo)
        {
            _serviceProvider = serviceProvider;
            _hubContext = hubContext;
            _browserLauncher = browserLauncher;
            _multiplexer = multiplexer;
            _portInfo = portInfo;
        }

        public async Task InitializeRadioAsync()
        {
            await ExecuteInitializationAsync(CancellationToken.None);
        }

        private async Task ExecuteInitializationAsync(CancellationToken stoppingToken)
        {
            ILogger<RadioInitializationService>? logger = null; // Make logger nullable
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                var multiplexer = _multiplexer;
                var radioStateService = scope.ServiceProvider.GetRequiredService<RadioStateService>();
                var statePersistence = scope.ServiceProvider.GetRequiredService<RadioStatePersistenceService>();
                logger = scope.ServiceProvider.GetRequiredService<ILogger<RadioInitializationService>>();

                var settings = await settingsService.GetSettingsAsync();

                // Check if COM port is configured - if not, redirect to Settings
                if (string.IsNullOrWhiteSpace(settings.SerialPort) || settings.SerialPort == "Not Set")
                {
                    logger.LogWarning("[RadioInitializationService] No COM port configured - redirecting to Settings");
                    AppStatus.InitializationStatus = "error";
                    await _hubContext.Clients.All.SendAsync("ShowSettingsPage");
                    if (!Debugger.IsAttached &&
                        string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase))
                    {
                        _browserLauncher.OpenOnce($"{_portInfo.RootUrl}/Settings");
                    }
                    return;
                }

                logger.LogInformation("Attempting to connect to radio on port {SerialPort} at baud {BaudRate}", settings.SerialPort, settings.BaudRate);

                try
                {
                    await multiplexer.ConnectAsync(settings.SerialPort, settings.BaudRate);
                }
                catch (Exception connEx)
                {
                    // COM port error (wrong port, port in use, etc.) - go to Settings
                    logger.LogError(connEx, "[RadioInitializationService] Failed to open COM port {SerialPort} - redirecting to Settings", settings.SerialPort);
                    AppStatus.InitializationStatus = "error";
                    await _hubContext.Clients.All.SendAsync("ShowSettingsPage");
                    if (!Debugger.IsAttached &&
                        string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase))
                    {
                        _browserLauncher.OpenOnce($"{_portInfo.RootUrl}/Settings");
                    }
                    return;
                }

                logger.LogInformation("Disabling auto information...");
                await multiplexer.DisableAutoInformationAsync();

                logger.LogInformation("Sending FA; command...");
                var faResponse = await multiplexer.SendCommandAsync("FA;", "Initialization", stoppingToken);
                if (string.IsNullOrWhiteSpace(faResponse) || !faResponse.StartsWith("FA"))
                {
                    // Radio not responding - likely OFF. Attempt to power on.
                    logger.LogWarning("[RadioInitializationService] Radio not responding to FA;. Attempting to power on...");
                    await multiplexer.SendCommandAsync("PS1;", "Initialization", stoppingToken); // Power ON
                    await Task.Delay(1200, stoppingToken); // Wait for radio to power up (empirical: 1.2s)
                    logger.LogInformation("Retrying FA; after power on attempt...");
                    faResponse = await multiplexer.SendCommandAsync("FA;", "Initialization", stoppingToken);

                    if (string.IsNullOrWhiteSpace(faResponse) || !faResponse.StartsWith("FA"))
                    {
                        // Still no response, treat as OFF
                        logger.LogWarning("[RadioInitializationService] Radio still not responding after power on attempt. User can power on via UI.");
                        radioStateService.RadioPowerOn = false;
                        AppStatus.InitializationStatus = "radio_off";
                        await _hubContext.Clients.All.SendAsync("InitializationStatus", "radio_off");

                        // Open Settings page if radio is off
                        if (!Debugger.IsAttached && 
                            string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase))
                        {
                            _browserLauncher.OpenOnce($"{_portInfo.RootUrl}/Settings");
                        }
                        return;
                    }
                    else
                    {
                        logger.LogInformation("[RadioInitializationService] Radio responded to FA; after power on attempt: {Response}", faResponse);
                    }
                }

                // Radio responded - it's ON
                radioStateService.RadioPowerOn = true;
                // Tell RadioStateService whether this is a single-receiver
                // model. The dispatcher uses this to route P1=0 ("Fixed" on
                // single-receiver radios) responses to whichever VFO is
                // currently active per VS, instead of always writing to *A
                // state. See #34 R2 controls-bleed fix.
                radioStateService.IsSingleReceiver = RadioCapabilities.IsSingleReceiver(settings.RadioModel);
                radioStateService.RadioModel = settings.RadioModel;
                logger.LogInformation("[RadioInitializationService] Radio responded to FA;: {Response}", faResponse);

                // Safety: force the radio into RX before doing anything else.
                // Yaesu HF rigs preserve MOX/TX state across power cycles in
                // some firmwares, so a radio powered off mid-transmit (by YWC,
                // WSJT-X via rigctld, or a stuck PTT) can come back up still
                // transmitting. If we don't clear it here, the next operation
                // happens on a live carrier.
                try
                {
                    await multiplexer.SendCommandAsync("TX0;", "Initialization", stoppingToken);
                    radioStateService.IsTransmitting = false;
                    logger.LogInformation("[RadioInitializationService] Sent TX0; safety RX-enforce on connect");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[RadioInitializationService] TX0; safety RX-enforce failed (non-fatal)");
                }

                // Send initialization commands and wait for DT0 response (with timeout).
                // CatMultiplexerService.InitializeRadioAsync() enforces its own internal
                // 5s wait for the DT0 response and throws TimeoutException directly if it
                // doesn't arrive (it takes no CancellationToken, so a CTS here can't touch
                // it). Some radios don't send a DT0 response at all — confirmed via
                // iu1teu's log on Issue #65: the FTDX3000 never responds to DT0;, so this
                // fired on every single connection attempt and (before this fix) the
                // TimeoutException fell through to the top-level catch-all below, aborting
                // the whole initialization and bouncing the user to Settings in a loop.
                logger.LogInformation("[RadioInitializationService] Sending full initialization sequence and waiting for DT0 (timeout 5s)...");

                try
                {
                    await multiplexer.InitializeRadioAsync();
                    logger.LogInformation("[RadioInitializationService] ✓ DT0 received, initialization sequence complete.");
                }
                catch (TimeoutException)
                {
                    logger.LogWarning("[RadioInitializationService] ⚠ Timeout waiting for DT0 response - continuing anyway");
                }

                // 2. Load persisted state from .json
                var persistedState = statePersistence.Load();
                logger.LogInformation("[RadioInitializationService] Persisted values before initialization: " +
                    "ModeA={ModeA}, ModeB={ModeB}, Power={Power}, AntennaA={AntennaA}, AntennaB={AntennaB}, MicGain={MicGain}",
                    persistedState.ModeA, persistedState.ModeB, persistedState.Power, persistedState.AntennaA, persistedState.AntennaB, persistedState.MicGain);

                // 3. Send only non-empty/non-zero values to the radio (parallelized)
                var stateTasks = new List<Task>();
                // Mode (MD) is deliberately NOT restored from persisted state on
                // connect (Issue #38, SP3L-Jacek 2026-06-18). The radio is the
                // source of truth: if the operator changed mode on the front
                // panel while YWC was closed, restoring YWC's last-saved value
                // would silently overwrite their setting.
                // The MD0;/MD1; queries (sent during InitializeRadioAsync's
                // fast burst, and again in readQueries below) populate YWC's UI
                // with whatever the radio currently has. Same anti-pattern as
                // RF Power (#35), MIC GAIN / Speech Processor / PROC LEVEL (#16).
                if (!string.IsNullOrEmpty(persistedState.AntennaA))
                {
                    stateTasks.Add(multiplexer.SendCommandAsync($"AN0{persistedState.AntennaA};", "Initialization", stoppingToken)
                        .ContinueWith(t => { if (!t.IsFaulted) radioStateService.AntennaA = persistedState.AntennaA; }));
                }
                if (!string.IsNullOrEmpty(persistedState.AntennaB))
                {
                    stateTasks.Add(multiplexer.SendCommandAsync($"AN1{persistedState.AntennaB};", "Initialization", stoppingToken)
                        .ContinueWith(t => { if (!t.IsFaulted) radioStateService.AntennaB = persistedState.AntennaB; }));
                }
                // Restore AF Gain — only on dual-receiver radios. On
                // single-receiver the radio is the source of truth (same
                // precedent as Mode #38, RF Power #35, MIC Gain #16); the
                // AG0;/AG1; readQueries and ping-pong AG0; populate the UI.
                if (!radioStateService.IsSingleReceiver)
                {
                    if (persistedState.AfGainA > 0 && persistedState.AfGainA <= 255)
                    {
                        stateTasks.Add(multiplexer.SendCommandAsync($"AG0{persistedState.AfGainA:D3};", "Initialization", stoppingToken)
                            .ContinueWith(t => { if (!t.IsFaulted) radioStateService.AfGainA = persistedState.AfGainA; }));
                    }
                    if (persistedState.AfGainB > 0 && persistedState.AfGainB <= 255)
                    {
                        stateTasks.Add(multiplexer.SendCommandAsync($"AG1{persistedState.AfGainB:D3};", "Initialization", stoppingToken)
                            .ContinueWith(t => { if (!t.IsFaulted) radioStateService.AfGainB = persistedState.AfGainB; }));
                    }
                }
                // MIC GAIN, Speech Processor ON/OFF, and PROC LEVEL are
                // deliberately NOT restored from persisted state on connect
                // (Issue #16, SP3L-Jacek 2026-06-04). These three values
                // directly affect TX audio quality and are normally tuned
                // physically on the radio for optimal sound — a fresh YWC
                // install was writing its default `50` / `50` / off back to
                // the radio, blowing away the operator's carefully-set
                // MIC GAIN=33 / PROC LEVEL=100. The radio is the source of
                // truth: the `MG;`, `PR;`, `PL;` queries in `readQueries`
                // below will populate YWC's UI with whatever the radio
                // actually has. If the user changes a value via the YWC
                // slider afterwards, that sends the command to the radio
                // and the radio's state changes accordingly.
                // IF Width is read from the radio on connect (SH queries below) — not written here.
                // IF Shift is also deliberately NOT restored from persisted state
                // on connect (Issue #41, SP3L-Jacek 2026-06-18). Same anti-pattern
                // as Mode / RF Power / MIC Gain. The IS0;/IS1; queries below
                // populate YWC's UI with the radio's actual current values.
                await Task.WhenAll(stateTasks);

                // 4. Read actual radio state (frequencies, band, etc.) before marking initialized
                logger.LogInformation("[RadioInitializationService] Reading actual radio state...");

                // Query VFO A frequency
                var faFreqResponse = await multiplexer.SendCommandAsync("FA;", "Initialization", stoppingToken);
                if (!string.IsNullOrWhiteSpace(faFreqResponse) && faFreqResponse.StartsWith("FA"))
                {
                    var freqStr = faFreqResponse.Substring(2).TrimEnd(';');
                    if (int.TryParse(freqStr, out int freqHz))
                    {
                        radioStateService.FrequencyA = freqHz;
                        logger.LogInformation("[RadioInitializationService] VFO A frequency: {FreqHz} Hz", freqHz);
                    }
                }

                // Query VFO B frequency  
                var fbFreqResponse = await multiplexer.SendCommandAsync("FB;", "Initialization", stoppingToken);
                if (!string.IsNullOrWhiteSpace(fbFreqResponse) && fbFreqResponse.StartsWith("FB"))
                {
                    var freqStr = fbFreqResponse.Substring(2).TrimEnd(';');
                    if (int.TryParse(freqStr, out int freqHz))
                    {
                        radioStateService.FrequencyB = freqHz;
                        logger.LogInformation("[RadioInitializationService] VFO B frequency: {FreqHz} Hz", freqHz);
                    }
                }

                // Query TX VFO (FT0 = VFO A is TX, FT1 = VFO B is TX)
                var ftResponse = await multiplexer.SendCommandAsync("FT;", "Initialization", stoppingToken);
                if (!string.IsNullOrWhiteSpace(ftResponse) && ftResponse.StartsWith("FT"))
                {
                    var txVfoStr = ftResponse.Substring(2).TrimEnd(';');
                    if (int.TryParse(txVfoStr, out int txVfo))
                    {
                        radioStateService.TxVfo = txVfo;
                        logger.LogInformation("[RadioInitializationService] TX VFO: {TxVfo} ({VfoName})", txVfo, txVfo == 0 ? "VFO A" : "VFO B");
                    }
                }

                // Query Split mode (ST0=off, ST1=on, ST2=on+5kHz)
                var stResponse = await multiplexer.SendCommandAsync("ST;", "Initialization", stoppingToken);
                if (!string.IsNullOrWhiteSpace(stResponse) && stResponse.StartsWith("ST"))
                {
                    if (int.TryParse(stResponse.Substring(2, 1), out int splitMode))
                    {
                        radioStateService.SplitMode = splitMode;
                        logger.LogInformation("[RadioInitializationService] Split mode: {SplitMode}", splitMode);
                    }
                }

                // Query VS (VFO Select) — which VFO is the active operating
                // (RX) VFO. Required for the single-receiver normal-mode
                // greying to flip when the user presses A/B on the radio's
                // front panel. See #34 R2 / dispatcher VS case.
                await multiplexer.SendCommandAndDispatchAsync("VS;", "Initialization", stoppingToken);

                // Query RX/TX clarifier on/off state (all models)
                var rtResponse = await multiplexer.SendCommandAsync("RT;", "Initialization", stoppingToken);
                if (!string.IsNullOrWhiteSpace(rtResponse) && rtResponse.StartsWith("RT"))
                {
                    if (int.TryParse(rtResponse.Substring(2, 1), out int rtVal))
                        radioStateService.RxClarOn = rtVal == 1;
                }
                var xtResponse = await multiplexer.SendCommandAsync("XT;", "Initialization", stoppingToken);
                if (!string.IsNullOrWhiteSpace(xtResponse) && xtResponse.StartsWith("XT"))
                {
                    if (int.TryParse(xtResponse.Substring(2, 1), out int xtVal))
                        radioStateService.TxClarOn = xtVal == 1;
                }

                // For FTdx10/FT-710: read per-VFO offsets via CF (dispatcher updates state)
                if (settings.RadioModel is "FTdx10" or "FT-710")
                {
                    await multiplexer.SendCommandAsync("CF001;", "Initialization", stoppingToken);
                    await multiplexer.SendCommandAsync("CF011;", "Initialization", stoppingToken);
                }

                // Read actual radio state — all dispatcher-handled commands.
                // These overwrite any defaults or persisted values with what the radio actually has.
                var readQueries = new[]
                {
                    // Mode (#38 — added 2026-06-18 to ensure mode is always read
                    // from the radio after the persisted-mode write was removed)
                    "MD0;", "MD1;",          // Mode A/B
                    // IF / filter
                    // SH is read with "SH{vfo};" — NOT "SH{vfo}0;". The leading-
                    // zero form "SH00;" / "SH10;" is interpreted by Yaesu radios
                    // as a SET-to-zero on that VFO, which is silently ignored
                    // (or worse). Reported as #40 by SP3L-Jacek 2026-06-18.
                    "SH0;", "SH1;",          // IF Width A/B
                    "IS0;", "IS1;",          // IF Shift A/B (#41 — was being
                                             //   overwritten with persisted value
                                             //   in stateTasks, now read instead)
                    // Receive controls A
                    "GT0;",                  // AGC A
                    "PA0;",                  // IPO/AMP A
                    "RA0;",                  // Attenuator A
                    "NR0;",                  // Noise Reduction A
                    "RL0;",                  // NR Level / DNR algorithm A (#47)
                    "NB0;",                  // Noise Blanker A
                    "NL0;",                  // NB Level A
                    "BC0;",                  // Auto Notch A
                    // Receive controls B
                    "GT1;",                  // AGC B
                    "PA1;",                  // IPO/AMP B
                    "RA1;",                  // Attenuator B
                    "NR1;",                  // Noise Reduction B
                    "RL1;",                  // NR Level / DNR algorithm B (#47)
                    "NB1;",                  // Noise Blanker B
                    "NL1;",                  // NB Level B
                    "BC1;",                  // Auto Notch B
                    // Manual Notch (#46 — never queried before, default 1000 Hz / OFF
                    // was shown regardless of radio state).
                    // BP{vfo}0{xxx}; reads the on/off; BP{vfo}1{xxx}; reads the
                    // frequency. The radio responds with the full BP message
                    // including parameter and value.
                    "BP00;", "BP01;",        // Manual Notch on/off + freq, VFO A
                    "BP10;", "BP11;",        // Manual Notch on/off + freq, VFO B
                    // Contour (#39 — never queried before, persisted value was
                    // always shown). CO command has four sub-parameters per VFO:
                    // 0=Contour on/off, 1=Contour freq, 2=APF on/off, 3=APF freq.
                    "CO00;", "CO01;", "CO02;", "CO03;",   // Contour + APF, VFO A
                    "CO10;", "CO11;", "CO12;", "CO13;",   // Contour + APF, VFO B
                    // RF Gain / Squelch
                    "RG0;", "RG1;",
                    "SQ0;", "SQ1;",
                    // Audio / TX
                    "AG0;", "AG1;",          // AF Gain A/B
                    "MG;",                   // MIC Gain
                    "PR;",                   // Speech Processor on/off
                    "PL;",                   // Processor Level
                    "PC;",                   // RF Power (Issue #35) — radio is
                                             //   source of truth on connect
                    "ML0;", "ML1;",          // Monitor on/off / level
                    // CW
                    "KP;",                   // CW Pitch
                    "KS;",                   // CW Speed
                    "BI;",                   // CW Break-in mode
                    "SD;",                   // CW Break-in delay
                    // VOX
                    "VX;",                   // VOX on/off
                    "VG;",                   // VOX Gain
                    "VD;",                   // VOX Delay
                };
                foreach (var q in readQueries)
                    await multiplexer.SendCommandAndDispatchAsync(q, "Initialization", stoppingToken);

                // 4b. Single-receiver "ping-pong" -- read the OTHER VFO's
                // P1=0-Fixed receive controls so YWC has both *A and *B
                // populated. Without this, the inactive panel shows defaults
                // (Jacek SP3L #34 pre5 — his proposed fix). Skipped on
                // dual-receiver since FTdx101 reports per-VFO via P1.
                if (radioStateService.IsSingleReceiver)
                {
                    var origVfo = radioStateService.ActiveVfo;
                    var otherVfo = 1 - origVfo;
                    logger.LogInformation(
                        "[RadioInitializationService] Single-receiver ping-pong: temporarily switching to VFO {Other} to read its stored receive controls",
                        otherVfo == 0 ? "A" : "B");

                    await _hubContext.Clients.All.SendAsync(
                        "VoiceStatusUpdate",
                        new { State = "ReadingRadioSettings", Message = $"Reading VFO {(otherVfo == 0 ? "A" : "B")} settings…" },
                        stoppingToken);
                    await _hubContext.Clients.All.SendAsync(
                        "RadioInfoStatus",
                        $"Reading VFO {(otherVfo == 0 ? "A" : "B")} settings…",
                        stoppingToken);

                    // Switch active VFO. The dispatcher's VS case updates
                    // RadioStateService.ActiveVfo synchronously when the
                    // response is dispatched.
                    await multiplexer.SendCommandAndDispatchAsync($"VS{otherVfo};", "Initialization", stoppingToken);
                    await Task.Delay(150, stoppingToken); // settle

                    // Re-query the P1=0-Fixed receive controls. Each response
                    // routes through SetPerVfo's 300 ms buffer; by the time
                    // the buffer flushes, ActiveVfo is still = otherVfo, so
                    // they land in the right slot.
                    foreach (var q in CatCommands.SingleReceiverPerVfoQueries)
                        await multiplexer.SendCommandAndDispatchAsync(q, "Initialization", stoppingToken);

                    // Wait for the buffered dispatches to drain (BufferDelayMs
                    // in CatMessageDispatcher = 300 ms) plus a little headroom.
                    await Task.Delay(400, stoppingToken);

                    // Swap back to the original VFO.
                    await multiplexer.SendCommandAndDispatchAsync($"VS{origVfo};", "Initialization", stoppingToken);
                    await Task.Delay(150, stoppingToken);

                    // Drain any belated broadcasts from the swap-back so they
                    // land in origVfo's slot before we mark init complete.
                    await Task.Delay(400, stoppingToken);

                    await _hubContext.Clients.All.SendAsync(
                        "RadioInfoStatus", "", stoppingToken);

                    logger.LogInformation(
                        "[RadioInitializationService] Ping-pong complete; both VFOs' receive-control state populated.");
                }

                // 5. Set IsInitialized = true FIRST to allow property changes to be persisted and broadcast
                radioStateService.IsInitialized = true;

                // Derive bands from the actual frequencies (must be AFTER IsInitialized = true)
                var bandA = radioStateService.GetBandFromFrequency(radioStateService.FrequencyA);
                var bandB = radioStateService.GetBandFromFrequency(radioStateService.FrequencyB);
                logger.LogInformation("[RadioInitializationService] Calculated bands from frequencies: A={BandA} ({FreqA} Hz), B={BandB} ({FreqB} Hz)",
                    bandA, radioStateService.FrequencyA, bandB, radioStateService.FrequencyB);

                radioStateService.SetBand("A", bandA);
                radioStateService.SetBand("B", bandB);
                logger.LogInformation("[RadioInitializationService] Bands set: A={BandA}, B={BandB}", bandA, bandB);

                // Backfill per-band antenna profiles when empty. Older
                // appsettings.user.json files have BandProfile entries that
                // pre-date the per-band antenna feature (Antenna="" by default)
                // — without this, users would see Antenna fields stay empty
                // until they manually clicked an antenna button on each band.
                // We only fill if Antenna is empty, never overwriting an
                // existing valid selection.
                try
                {
                    var profilesChanged = false;
                    if (!string.IsNullOrEmpty(bandA) && !string.IsNullOrEmpty(radioStateService.AntennaA))
                    {
                        if (!settings.BandProfilesA.TryGetValue(bandA, out var profA))
                            profA = new BandProfile();
                        if (string.IsNullOrEmpty(profA.Antenna))
                        {
                            profA.Antenna = radioStateService.AntennaA!;
                            settings.BandProfilesA[bandA] = profA;
                            profilesChanged = true;
                        }
                    }
                    if (!string.IsNullOrEmpty(bandB) && !string.IsNullOrEmpty(radioStateService.AntennaB))
                    {
                        if (!settings.BandProfilesB.TryGetValue(bandB, out var profB))
                            profB = new BandProfile();
                        if (string.IsNullOrEmpty(profB.Antenna))
                        {
                            profB.Antenna = radioStateService.AntennaB!;
                            settings.BandProfilesB[bandB] = profB;
                            profilesChanged = true;
                        }
                    }
                    if (profilesChanged)
                    {
                        await settingsService.SaveSettingsAsync(settings);
                        logger.LogInformation("[RadioInitializationService] Backfilled empty Antenna fields for current bands (A={BandA}/Ant{AntA}, B={BandB}/Ant{AntB})",
                            bandA, radioStateService.AntennaA, bandB, radioStateService.AntennaB);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[RadioInitializationService] Per-band antenna backfill failed (non-fatal)");
                }

                // 6. Enable auto information
                await multiplexer.EnableAutoInformationAsync();

                // 7. (Formerly) "Use USB audio for DATA modes" auto-config.
                // Removed 2026-06-01 after RealTerm testing confirmed the EX command
                // addresses we were sending were not REAR SELECT — they wrote a value
                // to some other menu item that happens to exist at 010416 etc.
                // The radio menu (071 REAR SELECT on FTdx101) must be set to USB
                // manually. See USER_MANUAL §15 (FAQ) for the user-facing note.

                logger.LogInformation("[RadioInitializationService] ✓ Radio connected, initialized, and Auto Information streaming enabled");
                radioStateService.IsConnected = true;
                AppStatus.InitializationStatus = "complete";
                logger.LogInformation("[RadioInitializationService] InitializationStatus set to complete");
                await _hubContext.Clients.All.SendAsync("InitializationStatus", "complete");

                // Initialize VC Tune preselector module in the background so it
                // never delays the "complete" status or the browser opener.
                // VCTuneModule.InitializeAsync sends VT CAT commands; if the radio
                // is slow to respond those round-trips must not block this path.
                if (RadioCapabilities.SupportsVCTuneMain(settings.RadioModel))
                {
                    var vcTune = _serviceProvider.GetRequiredService<VCTuneModule>();
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1500); // let AutoInfo streaming settle first
                        try
                        {
                            await vcTune.InitializeAsync(CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            // VCTuneModule logs internally; swallow here so the
                            // fire-and-forget task never becomes an unobserved exception.
                            _ = ex;
                        }
                    });
                }

                // On success, open main page only in Production and not under debugger
                if (!Debugger.IsAttached && 
                    string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase))
                {
                    _browserLauncher.OpenOnce(_portInfo.RootUrl);
                }
            }
            catch (Exception ex)
            {
                AppStatus.InitializationStatus = "error";
                logger?.LogError(ex, "[RadioInitializationService] Radio initialization failed");
                await _hubContext.Clients.All.SendAsync("InitializationStatus", "error");
                await _hubContext.Clients.All.SendAsync("ShowSettingsPage");

                // On failure, open settings page only in Production and not under debugger
                if (!Debugger.IsAttached &&
                    string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase))
                {
                    _browserLauncher.OpenOnce($"{_portInfo.RootUrl}/Settings");
                }
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await ExecuteInitializationAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // Prevent app crash - log the error and set status
                Console.WriteLine($"[RadioInitializationService] Fatal error: {ex.Message}");
                AppStatus.InitializationStatus = "error";

                // Try to open Settings page even if initialization completely failed
                try
                {
                    _browserLauncher.OpenOnce($"{_portInfo.RootUrl}/Settings");
                }
                catch { /* Ignore browser launch errors */ }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            // Log-fence so we can see in the console exactly which stage of
            // shutdown stalls if anything ever hangs in here.
            using var scopeForLogger = _serviceProvider.CreateScope();
            var logger = scopeForLogger.ServiceProvider.GetService<ILogger<RadioInitializationService>>();
            logger?.LogInformation("[RadioInit] StopAsync entered");

            // Safety: send TX0; before disconnecting so YWC never leaves the
            // radio in transmit. Some Yaesu firmwares preserve MOX/TX state
            // across power cycles, so a shutdown mid-transmit could otherwise
            // result in the radio coming back up still keying. Best-effort
            // with a 1 s timeout so a hung send can't stall host shutdown.
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(1));
                await _multiplexer.SendCommandAsync("TX0;", "RadioInit-Shutdown", cts.Token);
                logger?.LogInformation("[RadioInit] TX0; safety RX-enforce sent on shutdown");
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[RadioInit] TX0; on shutdown failed — non-fatal");
            }

            // FTdx101 power-meter restore (discussion #6, F1ubw). During normal
            // operation MeterPollingService sets the radio's front-panel meter
            // to MS13 (Comp + SWR) as the RM0 read workaround; without this
            // restore, quitting YWC leaves the FTdx101's Power needle hidden
            // until the operator power-cycles the radio or hits the METER button.
            //
            // Best-effort with a 1 s timeout so a hung send (e.g. radio
            // powered off, COM cable yanked) can't stall host shutdown.
            // Only FTdx101MP/D set MS13; other radios don't touch the meter.
            try
            {
                var settingsService = scopeForLogger.ServiceProvider.GetRequiredService<ISettingsService>();
                var settings = await settingsService.GetSettingsAsync();
                if (settings.RadioModel is "FTdx101MP" or "FTdx101D")
                {
                    logger?.LogInformation("[RadioInit] Sending MS01 to restore FTdx101 power meter");
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(1));
                    await _multiplexer.SendCommandAsync(
                        CatCommands.SetMeterPower + ";", "RadioInit-Shutdown", cts.Token);
                    logger?.LogInformation("[RadioInit] MS01 send completed");
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[RadioInit] MS01 send failed — non-fatal");
            }

            logger?.LogInformation("[RadioInit] Disconnecting multiplexer");
            await _multiplexer.DisconnectAsync();
            logger?.LogInformation("[RadioInit] Multiplexer disconnected, calling base.StopAsync");
            await base.StopAsync(cancellationToken);
            logger?.LogInformation("[RadioInit] StopAsync complete");
        }
    }
}