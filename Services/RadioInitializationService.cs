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

                // Send initialization commands and wait for DT0 response (with timeout)
                logger.LogInformation("[RadioInitializationService] Sending full initialization sequence and waiting for DT0 (timeout 5s)...");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cts.Token);

                try
                {
                    await multiplexer.InitializeRadioAsync();
                    logger.LogInformation("[RadioInitializationService] ✓ DT0 received, initialization sequence complete.");
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
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
                if (!string.IsNullOrEmpty(persistedState.ModeA))
                {
                    logger.LogInformation("About to send ModeA={ModeA} to radio", persistedState.ModeA);
                    stateTasks.Add(multiplexer.SendCommandAsync(CatCommands.FormatMode(persistedState.ModeA, false), "Initialization", stoppingToken)
                        .ContinueWith(t => { if (!t.IsFaulted) radioStateService.ModeA = persistedState.ModeA; }));
                }
                if (!string.IsNullOrEmpty(persistedState.ModeB))
                {
                    stateTasks.Add(multiplexer.SendCommandAsync(CatCommands.FormatMode(persistedState.ModeB, true), "Initialization", stoppingToken)
                        .ContinueWith(t => { if (!t.IsFaulted) radioStateService.ModeB = persistedState.ModeB; }));
                }
                // RF Power is deliberately NOT restored from persisted state
                // on connect (Issue #35, SP3L-Jacek 2026-06-14). The radio is
                // the source of truth: if the operator changed the front-panel
                // power knob while YWC was closed, restoring YWC's last-saved
                // value would silently overwrite their setting. Same pattern
                // as MIC GAIN / Speech Processor / PROC LEVEL (Issue #16).
                // The PC; query in readQueries below populates YWC's UI with
                // whatever the radio currently has. Front-panel changes while
                // YWC is running flow through the dispatcher's "PC" case.
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
                // Restore AF Gain — only if the user previously saved a non-zero value.
                // 0 is the int default (never saved), so sending AG0000; on every fresh start
                // would silence the radio.
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
                // IF Width is read from the radio on connect (SH queries after stateTasks) — not written here.
                // Restore IF Shift
                {
                    var signA = persistedState.IfShiftA >= 0 ? '+' : '-';
                    var absA = Math.Abs(persistedState.IfShiftA);
                    stateTasks.Add(multiplexer.SendCommandAsync($"IS00{signA}{absA:D4};", "Initialization", stoppingToken)
                        .ContinueWith(t => { if (!t.IsFaulted) radioStateService.IfShiftA = persistedState.IfShiftA; }));
                }
                {
                    var signB = persistedState.IfShiftB >= 0 ? '+' : '-';
                    var absB = Math.Abs(persistedState.IfShiftB);
                    stateTasks.Add(multiplexer.SendCommandAsync($"IS10{signB}{absB:D4};", "Initialization", stoppingToken)
                        .ContinueWith(t => { if (!t.IsFaulted) radioStateService.IfShiftB = persistedState.IfShiftB; }));
                }
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
                    // IF / filter
                    "SH00;", "SH10;",       // IF Width A/B
                    // Receive controls A
                    "GT0;",                  // AGC A
                    "PA0;",                  // IPO/AMP A
                    "RA0;",                  // Attenuator A
                    "NR0;",                  // Noise Reduction A
                    "NB0;",                  // Noise Blanker A
                    "NL0;",                  // NB Level A
                    "BC0;",                  // Auto Notch A
                    // Receive controls B
                    "GT1;",                  // AGC B
                    "PA1;",                  // IPO/AMP B
                    "RA1;",                  // Attenuator B
                    "NR1;",                  // Noise Reduction B
                    "NB1;",                  // Noise Blanker B
                    "NL1;",                  // NB Level B
                    "BC1;",                  // Auto Notch B
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