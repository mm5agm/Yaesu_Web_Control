namespace Yaesu_Web_Control.Models
{
    public class ApplicationSettings
    {
        // Connection Settings
        public string SerialPort { get; set; } = "COM3";
        public int BaudRate { get; set; } = 38400;
        public string WebAddress { get; set; } = "0.0.0.0"; // Bind to all interfaces

        // HTTP port the web server listens on. Default 8080. If that port is
        // already in use, the app tries 8081…8089 in turn at startup and uses
        // the first one that's free. User can pin a specific port here if they
        // know 8080 always clashes on their machine (e.g. Plex, Jenkins).
        public int HttpPort { get; set; } = 8080;

        public string RadioModel { get; set; } = "FTdx101MP"; // MP = dual receiver, D = single receiver


        // External Applications - Command Lines.
        // RULE: paths containing spaces MUST be wrapped in double quotes; any
        // text after the closing quote (or after the first space, for unquoted
        // paths) is passed to the launched process as command-line arguments.
        // See USER_MANUAL.md "External Applications" for examples.
        public string WsjtxCommandLine { get; set; } = @"C:\WSJT\wsjtx\bin\wsjtx.exe --rig-name=WebApp";
        public string JtalertCommandLine { get; set; } = @"C:\HamApps\JTAlert\JTAlert.exe";
        public string Log4omCommandLine { get; set; } = @"""C:\Program Files (x86)\Log4OM 2\Log4OM.exe""";
        public string GridtrackerCommandLine { get; set; } = @"""C:\Program Files\GridTracker2\GridTracker2.exe""";

        // External Applications - Custom Names (user can rename buttons)
        public string App1Name { get; set; } = "WSJT-X";
        public string App2Name { get; set; } = "JTAlert";
        public string App3Name { get; set; } = "Log4OM";
        public string App4Name { get; set; } = "GridTracker";

        // External Applications - Show/Hide buttons (optional apps)
        public bool ShowWsjtxButton { get; set; } = true;
        public bool ShowJtalertButton { get; set; } = true;
        public bool ShowLog4omButton { get; set; } = true;
        // Default off — most users won't have GridTracker installed
        public bool ShowGridtrackerButton { get; set; } = false;

        // WSJT-X UDP Settings
        // Default: Use the same multicast address as configured in WSJT-X
        // Common values: 224.0.0.1, 239.255.0.1, or 127.0.0.1 for unicast
        public string WsjtxUdpAddress { get; set; } = "239.255.0.1";
        public int WsjtxUdpPort { get; set; } = 2237;

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

        // CW keyer message memories M1-M5 (sent via KY command)
        public List<string> CwMessages { get; set; } = new() { "CQ CQ DE {CALL}", "TU 73", "QRZ?", "UR 5NN", "DE {CALL}" };

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

        // ── Alexa voice control ─────────────────────────────────────────────
        // The /api/alexa endpoint is the webhook Amazon Alexa POSTs voice-intent
        // requests to. It's disabled by default for two reasons:
        // 1. Most users will never set up the Cloudflare Tunnel + Amazon Developer
        //    Skill configuration that voice control requires — keeping the
        //    endpoint dormant means they're not accidentally exposing a
        //    voice-controlled rig surface.
        // 2. Until the Amazon-signature-verification work is finished, leaving
        //    this enabled with a public tunnel URL would mean anyone who learned
        //    the URL could send arbitrary intent requests.
        //
        // Set AlexaEnabled=true only after VOICE_CONTROL.md Phase 3 (Skill setup)
        // is done and you have signature verification configured.
        // See VOICE_CONTROL.md for the full setup.
        public bool AlexaEnabled { get; set; } = false;

        // DEVELOPMENT ONLY. When true, the /api/alexa endpoint accepts requests
        // without verifying the Amazon SHA-256 signature. Useful for local
        // testing via curl/Postman while building intent handlers. Production
        // installs MUST leave this false — otherwise anyone who finds the
        // public tunnel URL can drive the radio by sending fake JSON requests.
        public bool AlexaSkipSignatureVerification { get; set; } = false;
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

    // Stores per-band IF Width/Shift/Mode so they are restored when the operator returns to a band.
    public class BandProfile
    {
        public string IfWidthCode { get; set; } = "";
        public int IfShiftHz { get; set; } = 0;
        public string Mode { get; set; } = "";
    }
}