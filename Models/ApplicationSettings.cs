namespace Yaesu_Web_Control.Models
{
    public class ApplicationSettings
    {
        // Connection Settings
        public string SerialPort { get; set; } = "COM3";
        public int BaudRate { get; set; } = 38400;

        // Delay between meter-poll cycles in milliseconds. Lower gives a faster
        // S-meter but increases CAT traffic on a bus shared with rigctld/WSJT-X.
        // Valid range: 50–1000. Default 200.
        public int MeterPollIntervalMs { get; set; } = 200;
        public string WebAddress { get; set; } = "0.0.0.0"; // Bind to all interfaces

        // HTTP port the web server listens on. Default 8080. If that port is
        // already in use, the app tries 8081…8089 in turn at startup and uses
        // the first one that's free. User can pin a specific port here if they
        // know 8080 always clashes on their machine (e.g. Plex, Jenkins).
        public int HttpPort { get; set; } = 8080;

        /// <summary>
        /// When true (default), the host exits ~30s after the last heartbeating
        /// browser tab disconnects. Set false to keep the process running with
        /// no browser connected (useful on macOS console hosts and headless shacks).
        /// </summary>
        public bool AutoShutdownWhenNoBrowsers { get; set; } = true;

        /// <summary>
        /// When true (default), the host opens the default browser to the control
        /// panel URL once after Kestrel starts. Set false to start quietly —
        /// open the UI from the system tray / menu bar, or browse to the URL
        /// yourself. Docker / containers always skip auto-open regardless.
        /// </summary>
        public bool OpenBrowserOnStartup { get; set; } = true;

        public string RadioModel { get; set; } = "FTdx101MP"; // MP = dual receiver, D = single receiver

        /// <summary>
        /// When true, the log file records Debug as well as Information — every
        /// CAT command sent, every reply received, every state save and every
        /// browser status poll. Default OFF: on a live radio that is ~85,000
        /// extra lines and ~12 MB a day, almost none of which anyone ever reads.
        ///
        /// The normal log is NOT switched off by this, only trimmed. An
        /// opt-in-only log is never present for the first occurrence of an
        /// intermittent fault, which is the occurrence that gets reported.
        ///
        /// Applied live via <see cref="Services.LogLevelController"/> — no
        /// restart, because restarting to enable logging can destroy the state
        /// that caused the bug.
        /// </summary>
        public bool DetailedLogging { get; set; } = false;


        // External Applications - Command Lines.
        // RULE: paths containing spaces MUST be wrapped in double quotes; any
        // text after the closing quote (or after the first space, for unquoted
        // paths) is passed to the launched process as command-line arguments.
        // See USER_MANUAL.md "External Applications" for examples.
        public string WsjtxCommandLine { get; set; } = @"C:\WSJT\wsjtx\bin\wsjtx.exe --rig-name=WebApp";
        public string JtalertCommandLine { get; set; } = @"C:\HamApps\JTAlert\JTAlert.exe";
        public string Log4omCommandLine { get; set; } = @"""C:\Program Files (x86)\Log4OM 2\Log4OM.exe""";
        public string GridtrackerCommandLine { get; set; } = @"""C:\Program Files\GridTracker2\GridTracker2.exe""";
        public string FldigiCommandLine { get; set; } = @"""C:\Program Files\Fldigi-4.2.11\fldigi.exe""";

        // External Applications - Custom Names (user can rename buttons)
        public string App1Name { get; set; } = "WSJT-X";
        public string App2Name { get; set; } = "JTAlert";
        public string App3Name { get; set; } = "Log4OM";
        public string App4Name { get; set; } = "GridTracker";
        public string App5Name { get; set; } = "Fldigi";

        // External Applications - Show/Hide buttons (optional apps)
        public bool ShowWsjtxButton { get; set; } = true;
        public bool ShowJtalertButton { get; set; } = true;
        public bool ShowLog4omButton { get; set; } = true;
        // Default off — most users won't have GridTracker installed
        public bool ShowGridtrackerButton { get; set; } = false;
        // Default off — most users won't have Fldigi installed
        public bool ShowFldigiButton { get; set; } = false;

        // WSJT-X UDP Settings
        // Default: Use the same multicast address as configured in WSJT-X
        // Common values: 224.0.0.1, 239.255.0.1, or 127.0.0.1 for unicast
        public string WsjtxUdpAddress { get; set; } = "239.255.0.1";
        public int WsjtxUdpPort { get; set; } = 2237;

        // When false, YWC does NOT bind the WSJT-X UDP port at startup, leaving
        // it free for another WSJT-X tool. Default true (unchanged behaviour) —
        // see WsjtxUdpService, which returns early when this is off.
        public bool WsjtxIntegrationEnabled { get; set; } = true;

        // Last Radio State (persisted between sessions)
        public RadioState LastRadioState { get; set; } = new();

        // Band Plan
        public string BandPlan { get; set; } = "Region1";

        // SDR Spectrum Display — per-VFO device assignment (v2.3.0+).
        //
        // SdrDeviceKeyA / SdrDeviceKeyB identify which physical SDR is wired
        // to each VFO's IF output. Each may be empty if that VFO has no SDR.
        // Both nullable to avoid the implicit [Required] from <Nullable>enable</Nullable>.
        //
        // SDRplay-format keys: "sdrplay:hw<N>-<serial>" (v2.3.0+) or the
        // legacy "sdrplay:<serial>" (still accepted; auto-migrated on save).
        // SoapySDR-format keys: "driver=rtlsdr,serial=00000001" etc.
        //
        // The SDRplay API enforces one device per process, so YWC spawns a
        // dedicated Yaesu_Sdr_Worker.exe process per SDR — see
        // docs/decisions/0001-dual-sdr-architecture.md.
        //
        // Migration from v2.2.x: SettingsService.AutoMigrateSdrFields auto-
        // promotes any value found in the legacy SdrDeviceKey property into
        // SdrDeviceKeyA on first read.
        public string? SdrDeviceKeyA { get; set; } = string.Empty;
        public string? SdrDeviceKeyB { get; set; } = string.Empty;

        // Legacy v2.2.x single-device field. KEPT as a hidden migration anchor:
        // SettingsService reads this and copies into SdrDeviceKeyA if A is empty,
        // then the next save writes SdrDeviceKey as empty so the file gradually
        // converges on the new shape. Do not reference outside SettingsService.
        public string? SdrDeviceKey { get; set; } = string.Empty;

        // Per-VFO sample rate (v2.3.0+). Each VFO's SDR can run at a different
        // span — typically 2 MHz on the calling band and 250 kHz zoomed in on
        // the working frequency. Default 0 is a "not set" sentinel so the
        // SettingsService migration can tell whether the value came from the
        // file or is the property's initial value — see MigrateSdrSampleRate.
        // After migration these are always > 0.
        public double SdrSampleRateHzA { get; set; } = 0;
        public double SdrSampleRateHzB { get; set; } = 0;

        // Legacy v2.2.x single sample rate. KEPT as a hidden migration anchor:
        // SettingsService reads this and copies into both A and B if those
        // are still 0, then writes 0 on next save. Do not reference outside
        // SettingsService.
        public double SdrSampleRateHz { get; set; } = 0;

        public long SdrIfFrequencyHz { get; set; } = 9_000_000;
        public int SdrFftSize { get; set; } = 1024;

        // Per-VFO spectrum DSP knobs (see SpectrumProcessor). Live-controlled
        // from the three sliders on each spectrum panel; persisted here so
        // settings survive YWC restarts and re-apply to a new worker session.
        //
        //   Gain    — pre-dB linear gain G (design doc §4.1). 1.0 = no boost.
        //   LowDb   — display clamp lower bound (SDR Console "Low"). Bins below
        //             this are pinned. Default -120 = full SDR range.
        //   HighDb  — display clamp upper bound (SDR Console "High"). Default
        //             0 = full SDR range.
        public float SdrSpectrumGainA   { get; set; } = 1.0f;
        public float SdrSpectrumGainB   { get; set; } = 1.0f;
        public float SdrSpectrumLowDbA  { get; set; } = -120f;
        public float SdrSpectrumLowDbB  { get; set; } = -120f;
        public float SdrSpectrumHighDbA { get; set; } = 0f;
        public float SdrSpectrumHighDbB { get; set; } = 0f;

        // Optional user override for the SDRplay API install directory
        // (the folder that contains the x64\sdrplay_api.dll subfolder).
        // Leave blank for auto-detect: SdrplayDllResolver tries the app
        // directory, then standard Program Files locations. Only needed
        // when the SDRplay API was installed to a non-default location
        // AND its bin folder wasn't added to PATH.
        public string? SdrplayInstallPath { get; set; } = string.Empty;

        // CW keyer message memories M1-M5 (sent via KY command)
        public List<string> CwMessages { get; set; } = new() { "CQ CQ DE {CALL}", "TU 73", "QRZ?", "UR 5NN", "DE {CALL}" };

        // Reader Mode's target IF width, in Hz. The reader decodes far better
        // through a narrow filter than through a wide one full of adjacent
        // signals, and 250 Hz is narrow enough to help without being so narrow
        // that a slightly mistuned signal falls outside it. Configurable
        // because the right answer depends on the operator's ear as much as on
        // the radio: someone who tunes precisely may prefer 100, and someone
        // working a crowded band by ear may want 400. The radio is asked for
        // the nearest width it actually has - see YaesuIfWidth.CodeForHz.
        public int CwReaderFilterHz { get; set; } = 250;

        // Whether Reader Mode also switches the audio peak filter on. APF
        // narrows further still, in the audio domain, and it is the single
        // biggest improvement available to a decoder on a weak signal. Left
        // switchable because APF rings, and an operator listening as well as
        // reading may not want it.
        public bool CwReaderUseApf { get; set; } = true;

        // Per-band IF Width/Shift/Mode memory — keyed by band name (e.g. "20m")
        public Dictionary<string, BandProfile> BandProfilesA { get; set; } = new();
        public Dictionary<string, BandProfile> BandProfilesB { get; set; } = new();

        // DX cluster settings. No default host — the user picks their cluster
        // explicitly from Settings before enabling. Empty host = feature off.
        // The string properties below are nullable to avoid the implicit [Required]
        // from <Nullable>enable</Nullable>; without that, an empty input on the
        // Settings page makes jQuery unobtrusive validation block form submission
        // client-side with no visible error, so Save Settings appears to do nothing.
        public bool DxClusterEnabled { get; set; } = false;
        public string? DxClusterHost { get; set; } = "";
        public int DxClusterPort { get; set; } = 7300;
        public string? DxClusterLoginCallsign { get; set; } = "";
        public int DxSpotAgeMinutes { get; set; } = 15;

        // Cluster commands to send each time we log in. One per line.
        // Useful for set/qra IO85CX, set/name Colin, set/filter, set/skimmer etc.
        // Commands are sent in order after the callsign is accepted.
        public string? DxClusterPostLoginCommands { get; set; } = "";

        // Callsigns or callsign prefixes to watch. Each line is matched
        // case-insensitively. A trailing * makes it a prefix match
        // ("G4*" matches G4ABC, G4XYZ). No wildcards = exact match.
        // Lines starting with # are ignored. Empty = feature off.
        public string? DxClusterWatchedCallsigns { get; set; } = "";

        // Optional roofing filters installed in the radio (FTdx101MP/D only).
        // "6"=12kHz and "7"=3kHz are always fitted. "8"=1.2kHz, "9"=600Hz, "A"=300Hz are optional.
        // FTdx10 has fixed roofing filters and ignores this setting.
        public List<string> InstalledRoofingFilters { get; set; } = new() { "6", "7", "8", "9", "A" };

        // ── Accessibility ─────────────────────────────────────────────────
        // When true, the VFO frequency displays show up/down arrow buttons
        // alongside the digit display so users who can't use a mouse wheel
        // (head-tracking + on-screen keyboard, mouse-only users with reduced
        // dexterity) can step the selected digit by clicking. Default OFF
        // so users with mouse wheels see the uncluttered default layout.
        // Yuri W4YSW request 2026-06-17.
        public bool ShowFrequencyArrowButtons { get; set; } = false;

        // Browser key that toggles TX (transmit). Empty / null = disabled.
        // Stored as a KeyboardEvent.key value such as "t" or "F8", except
        // Space which is stored as the token "Space" (a lone " " cannot
        // survive HTML form / input value round-trips).
        // Ignored while typing in inputs to avoid accidental keying during
        // form entry or frequency editing.
        // Nullable so an empty input does not get an implicit [Required] from
        // <Nullable>enable</Nullable> — that would make jQuery unobtrusive
        // validation silently block the entire Settings form (same class of
        // bug as #65 / SdrplayInstallPath and the DX-cluster fields).
        public string? TxToggleKey { get; set; } = "";

        // ── Voice Control (in-process SAPI) ───────────────────────────────
        // When true, the navbar mic button is shown and the SAPI recogniser
        // engages on PTT. Default OFF -- voice control is opt-in so users
        // who didn't ask for it don't get surprise mic-permission prompts
        // and don't pay the small CPU cost of holding a SAPI engine alive.
        // v2.4.0 feature (replaces the parked Alexa work). See
        // docs/VoiceControl/v1-plan.md.
        public bool VoiceControlEnabled { get; set; } = false;

        // After every recognised voice command, speak a confirmation phrase
        // ("Move to fourteen point zero seven four megahertz, successful")
        // back through the PC's default audio output. Default ON when voice
        // control is enabled -- key accessibility feature for partially-
        // sighted operators (Yuri W4YSW, Thomas OZ1JTE) who can't see the
        // screen to know whether a CAT command landed. Users who find it
        // chatty can disable here without disabling voice control itself.
        public bool VoiceSpokenConfirmationEnabled { get; set; } = true;

        // Step size used by the NudgeUp / NudgeDown voice commands (Hz),
        // independently per VFO -- each VFO's mic button on the Index page
        // has its own step-size dropdown next to it. Options exposed: 10,
        // 100, 1000, 10000, 100000. VFO B is only reachable on dual-receiver
        // radios (RadioCapabilities.IsDualReceiver); the field still exists
        // for single-receiver radios but nothing writes/reads it there.
        public long VoiceNudgeStepHzA { get; set; } = 10_000;
        public long VoiceNudgeStepHzB { get; set; } = 10_000;

        // When true, Custom Command (macro) CAT strings are accepted
        // regardless of prefix during validation. Default OFF: a Custom
        // Command's CAT string must start with a prefix one of the built-in
        // Core Commands already trusts (see VoicePhraseValidator's CAT
        // allowlist) -- "recombine the primitives the app already trusts,
        // don't grant new ones." This is the gate an imported/shared voice
        // pack's CAT strings pass through; see
        // docs/VoiceControl/language-pack-manager-design.md §5.5.
        public bool VoiceAdvancedModeEnabled { get; set; } = false;

        // Active recognition locale (BCP-47, e.g. "en-GB"). Distinct from
        // "installed languages": a user can have several packs installed
        // under Grammars\<culture>\ and only one active at a time -- SAPI's
        // SpeechRecognitionEngine is constructed for a single culture (see
        // VoiceControlService.SwitchLocaleAsync). Switching doesn't require
        // an app restart. Defaults to en-GB since that's the only pack that
        // ships today. docs/VoiceControl/language-pack-manager-design.md §4.4.
        public string VoiceActiveLocale { get; set; } = "en-GB";

        // Recording device the speech recogniser listens to (MME product name,
        // e.g. "Microphone (USB Audio Device)"). Empty = Windows default
        // recording device. System.Speech can only bind to the default device
        // or a raw stream, so a chosen device is captured ourselves and fed to
        // SAPI via SetInputToAudioStream (see Services/Voice/MicrophoneCapture.cs).
        // Name-keyed rather than index-keyed because a device's index shifts as
        // others are plugged/unplugged, whereas the picked name is stable.
        public string VoiceInputDeviceName { get; set; } = "";

        // Playback device the spoken confirmation announcements play through
        // (MME product name). Empty = Windows default playback device. If the
        // Windows default is claimed by something else (WSJT-X, rig audio) the
        // operator may never hear confirmations, so they can point them at their
        // own speakers/headset here. System.Speech can't target an output device
        // by name, so the chosen-device path renders the phrase to a WAV and
        // plays it via NAudio (see Services/Voice/AudioOutput.cs, VoiceTtsService).
        public string VoiceOutputDeviceName { get; set; } = "";

        // ── Remote Audio (browser ↔ radio USB) ─────────────────────────────
        // Opt-in Opus/PCM bridge so a remote browser can hear radio RX and send
        // mic audio into radio TX over the existing LAN/VPN path. Off by default.
        public bool AudioStreamingEnabled { get; set; } = false;

        /// <summary>
        /// PortAudio capture device for radio RX (USB recording). Required when
        /// <see cref="AudioStreamingEnabled"/> — no OS-default fallback (wrong mic).
        /// Stored as <c>{name} [{hostApi}]</c> (e.g. <c>Microphone (USB Audio CODEC) [Windows WASAPI]</c>)
        /// so Windows duplicates across MME/WASAPI/DirectSound/WDM-KS stay distinct.
        /// Legacy bare names still resolve.
        /// </summary>
        public string? AudioRadioRxDevice { get; set; } = "";

        /// <summary>
        /// PortAudio playback device for radio TX (USB playback). Required when
        /// <see cref="AudioStreamingEnabled"/> — blank must not fall back to PC
        /// speakers (browser mic feedback). Same <c>{name} [{hostApi}]</c> key
        /// format as <see cref="AudioRadioRxDevice"/>.
        /// </summary>
        public string? AudioRadioTxDevice { get; set; } = "";

        public float AudioRxGain { get; set; } = 1.0f;
        public float AudioTxGain { get; set; } = 1.0f;

        // ── Radio Display (USB UVC / HDMI capture → MJPEG) ────────────────
        // Opt-in panel that grabs frames from a USB webcam or HDMI capture
        // dongle and serves them as MJPEG. Off by default. Tuned for Pi-class
        // hosts and ~800×480/600 radio panels (see VideoMaxWidth / FPS / quality).
        public bool VideoDisplayEnabled { get; set; } = false;

        /// <summary>
        /// Capture device key from <c>/api/video/devices</c>
        /// (<c>index:N</c>, or macOS <c>uid:…</c>). Empty = no device selected.
        /// </summary>
        public string? VideoCaptureDeviceKey { get; set; } = "";

        /// <summary>
        /// Downscale frames wider than this before JPEG encode. 0 = no downscale.
        /// Default 800 matches FTDX-10-class panels and avoids 1080p encode cost on a Pi.
        /// </summary>
        public int VideoMaxWidth { get; set; } = 800;

        /// <summary>
        /// Capture size for the Radio Display, as <c>"WxH"</c>. Empty (the
        /// default) means automatic — the ranked pin pick, which is right for
        /// almost everyone. An explicit value pins the capture to that MJPEG
        /// mode and becomes the encode width, overriding
        /// <see cref="VideoMaxWidth"/>. A value the current device does not
        /// offer falls back to automatic rather than failing to open.
        /// </summary>
        public string? VideoCaptureSize { get; set; } = "";

        /// <summary>
        /// Target encode rate. Allowed: 15, 30, 60 (Radio Display panel).
        /// Rates above the capture device's advertised maximum are hidden.
        /// </summary>
        public int VideoTargetFps { get; set; } = 15;

        /// <summary>
        /// JPEG quality. Allowed: 40, 65, 85 (Low / Medium / Max on the Radio Display panel).
        /// Default 85 (Max): keep the capture JPEG. Low/Medium recompress.
        /// </summary>
        public int VideoJpegQuality { get; set; } = 85;

        // ── Optional HTTPS (self-signed; restart to apply) ────────────────
        // Required for getUserMedia from a remote browser (secure context).
        public bool HttpsEnabled { get; set; } = false;
        public int HttpsPort { get; set; } = 8443;

        /// <summary>
        /// Extra SAN hostnames/IPs (newline or comma separated) baked into the
        /// self-signed cert — e.g. WireGuard IP or LAN hostname.
        /// </summary>
        public string? HttpsSanHosts { get; set; } = "";
    }

    public class RadioState
    {
        // VFO-A State
        public long FrequencyA { get; set; } = 14074000; // Default: 14.074 MHz (FT8)
        public string ModeA { get; set; } = "USB";
        public string AntennaA { get; set; } = "1";

        // VFO-B State
        public long FrequencyB { get; set; } = 14074000; // Default: 14.074 MHz (FT8)
        public string ModeB { get; set; } = "USB";
        public string AntennaB { get; set; } = "1";

        // IF Width/Shift
        public string IfWidthA { get; set; } = "8";
        public string IfWidthB { get; set; } = "8";
        public int IfShiftA { get; set; } = 0;
        public int IfShiftB { get; set; } = 0;

        // RF Gain (read from radio on connect — default 255 = max gain)
        public int RfGainA { get; set; } = 255;
        public int RfGainB { get; set; } = 255;

        // Squelch (read from radio on connect — default 0 = open)
        public int SquelchA { get; set; } = 0;
        public int SquelchB { get; set; } = 0;
    }

    // Stores per-band IF Width/Shift/Mode/Antenna so they are restored when the operator returns to a band.
    public class BandProfile
    {
        public string IfWidthCode { get; set; } = "";
        public int IfShiftHz { get; set; } = 0;
        public string Mode { get; set; } = "";
        // Antenna selection ("1" / "2" / "3"). Empty for legacy profiles
        // saved before this field existed — guard restore with IsNullOrEmpty.
        public string Antenna { get; set; } = "";
    }
}