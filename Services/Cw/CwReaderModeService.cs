using Yaesu_Web_Control.Models;

namespace Yaesu_Web_Control.Services.Cw
{
    /// <summary>
    /// Reader Mode: one button that sets the radio up the way the decoder needs
    /// it, and puts it back afterwards.
    ///
    /// The measured case for this is in the plan's section 1.5. The FTdx101's
    /// own built-in decoder failed on a signal the operator could copy by ear
    /// with the filters at 3 kHz, and was still poor after they were narrowed
    /// to 600 Hz. What the decoder is fed matters more than how it decodes, and
    /// a 2.4 kHz filter full of adjacent signals will defeat any decoder there
    /// is. So: CW mode, a narrow filter, APF on.
    ///
    /// <b>The restore is the reason this lives on the server.</b> The obvious
    /// implementation is three fetch calls from the browser, and it works right
    /// up until the operator reloads the page - at which point the record of
    /// what their filter used to be is gone with the tab, and they are left
    /// with a 250 Hz filter and APF ringing and nothing to press to undo it.
    /// Holding the previous settings here means the button still works after a
    /// reload, from a second browser, or from the voice control.
    ///
    /// This is per-radio code and stays in the app: the CAT frames, the SH code
    /// tables and the APF encoding are all Yaesu's. The core knows nothing
    /// about it.
    /// </summary>
    public sealed class CwReaderModeService
    {
        private readonly ICatClient _cat;
        private readonly RadioStateService _state;
        private readonly ISettingsService _settings;
        private readonly ILogger<CwReaderModeService> _logger;

        // One at a time. Enabling and restoring both read the radio, decide
        // from what came back, and write - so two of them interleaved could
        // save the settings Reader Mode had just applied as if they were the
        // operator's own, and restore the radio to 250 Hz and APF for ever.
        private readonly SemaphoreSlim _gate = new(1, 1);

        private Saved? _saved;

        public CwReaderModeService(ICatClient cat,
                                   RadioStateService state,
                                   ISettingsService settings,
                                   ILogger<CwReaderModeService> logger)
        {
            _cat      = cat;
            _state    = state;
            _settings = settings;
            _logger   = logger;
        }

        /// <summary>What the radio was set to before Reader Mode touched it.</summary>
        private sealed record Saved(string? Mode, string? IfWidthCode, bool ApfOn, int ApfFreqHz);

        public bool IsOn => _saved is not null;

        /// <summary>
        /// Sets CW mode, a narrow filter and APF, remembering what was there
        /// before. Enabling twice is a no-op rather than an error - it would
        /// otherwise save Reader Mode's own settings over the operator's.
        /// </summary>
        public async Task<CwReaderModeStatus> EnableAsync(int? filterHz = null, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (_saved is not null) return Describe("Already on.");

                var settings = await _settings.GetSettingsAsync();
                await EnsureConnectedAsync(settings);

                // Read the radio rather than trusting the cached state. Mode
                // and filter are not on the meter poll's fast tier, so if the
                // operator has just turned the filter knob the cached values
                // can be a second or two stale - and a stale value here is not
                // a stale display, it is what gets put back afterwards.
                await ReadCurrentAsync(settings, ct);

                _saved = new Saved(_state.ModeA, _state.IfWidthA, _state.ApfOnA, _state.ApfFreqA);
                _logger.LogInformation(
                    "Reader Mode on: saving mode {Mode}, IF width code {Width}, APF {Apf}",
                    _saved.Mode, _saved.IfWidthCode, _saved.ApfOn ? "on" : "off");

                int wantedHz = filterHz ?? settings.CwReaderFilterHz;
                await ApplyAsync(settings,
                                 mode:     IsCw(_saved.Mode) ? _saved.Mode : "CW-U",
                                 widthHz:  wantedHz,
                                 widthCode: null,
                                 apfOn:    settings.CwReaderUseApf,
                                 apfHz:    _saved.ApfFreqHz,
                                 ct: ct);

                return Describe("Reader Mode on.");
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Puts back what was there before. Restoring when it was never enabled
        /// does nothing, deliberately: the reader panel calls this when it
        /// closes, and closing a panel the operator never put into Reader Mode
        /// must not change their radio.
        /// </summary>
        public async Task<CwReaderModeStatus> RestoreAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var saved = _saved;
                if (saved is null) return Describe("Reader Mode was not on.");

                var settings = await _settings.GetSettingsAsync();
                await EnsureConnectedAsync(settings);

                await ApplyAsync(settings,
                                 mode:      saved.Mode,
                                 widthHz:   null,
                                 widthCode: saved.IfWidthCode,
                                 apfOn:     saved.ApfOn,
                                 apfHz:     saved.ApfFreqHz,
                                 ct: ct);

                // Cleared last. If a write threw half way through, the operator
                // still has a button that will try the restore again, which is
                // more use than a service that believes it already has.
                _saved = null;
                _logger.LogInformation("Reader Mode off: restored mode {Mode}, IF width code {Width}",
                                       saved.Mode, saved.IfWidthCode);

                return Describe("Reader Mode off. Your settings are back.");
            }
            finally { _gate.Release(); }
        }

        public CwReaderModeStatus Status() => Describe(IsOn ? "Reader Mode on." : "Reader Mode off.");

        // ---- the radio ----------------------------------------------------

        /// <summary>
        /// Mode, then width, then APF - and the order is not cosmetic.
        ///
        /// An SH code means different bandwidths in different modes: code 8 is
        /// 1650 Hz in SSB and 400 Hz in CW. Sending the width before the mode
        /// sets a width for the mode the radio is about to leave. And a mode
        /// change makes the radio restore its own per-mode Contour and APF,
        /// overriding anything we set first - which is why APF goes last.
        /// </summary>
        private async Task ApplyAsync(ApplicationSettings settings,
                                      string? mode,
                                      int? widthHz,
                                      string? widthCode,
                                      bool apfOn,
                                      int apfHz,
                                      CancellationToken ct)
        {
            string p1 = RadioCapabilities.VfoP1(_state.IsSingleReceiver, "A");

            if (!string.IsNullOrWhiteSpace(mode) && ModeCode(mode) is { } code)
            {
                await _cat.SendCommandAsync($"MD{RadioCapabilities.ModeP1("A")}{code};", "CwReader", ct);
                _state.ModeA = mode;
            }

            string? sh = widthCode;
            if (sh is null && widthHz is { } hz)
            {
                int? found = YaesuIfWidth.CodeForHz(settings.RadioModel, mode, hz);

                // Null means the table does not know this radio, not that the
                // radio has no narrow filter. Leaving the filter alone is the
                // honest response: a guessed SH code would set a bandwidth
                // nobody chose, on a rig nobody here has tested against.
                if (found is null)
                {
                    _logger.LogWarning(
                        "Reader Mode: no IF width table for {Model} in {Mode}, leaving the filter alone",
                        settings.RadioModel, mode);
                }
                else
                {
                    sh = found.Value.ToString();
                }
            }

            if (sh is not null && int.TryParse(sh, out int shCode))
            {
                await _cat.SendCommandAsync($"SH{p1}0{shCode:D2};", "CwReader", ct);
                _state.IfWidthA = sh;
            }

            await SetApfAsync(settings, p1, apfOn, apfHz, ct);
        }

        /// <summary>
        /// APF, in the two encodings the range uses. Lifted from CatController's
        /// own APF endpoint rather than called into it - a controller is not a
        /// service, and reaching into one from here would drag its request
        /// semaphore and its ModelState along with it.
        /// </summary>
        private async Task SetApfAsync(ApplicationSettings settings, string p1, bool on, int hz, CancellationToken ct)
        {
            if (settings.RadioModel == "FTDX3000")
            {
                await _cat.SendCommandAsync($"CO00{(on ? "02" : "00")};", "CwReader", ct);
                int vv3000 = Math.Clamp((hz / 25) + 10, 0, 20);
                await _cat.SendCommandAsync($"CO02{vv3000:D2};", "CwReader", ct);
            }
            else
            {
                int vvvv = Math.Clamp((hz / 10) + 25, 0, 50);
                await _cat.SendCommandAsync($"CO{p1}2000{(on ? 1 : 0)};", "CwReader", ct);
                await _cat.SendCommandAsync($"CO{p1}3{vvvv:D4};", "CwReader", ct);
            }

            _state.ApfOnA   = on;
            _state.ApfFreqA = hz;
        }

        /// <summary>
        /// Asks the radio what it is set to, and lets the dispatcher put the
        /// answers into RadioStateService the way it does for every other read.
        /// </summary>
        private async Task ReadCurrentAsync(ApplicationSettings settings, CancellationToken ct)
        {
            string p1 = RadioCapabilities.VfoP1(_state.IsSingleReceiver, "A");

            await _cat.SendCommandAsync($"MD{RadioCapabilities.ModeP1("A")};", "CwReader", ct);
            await _cat.SendCommandAsync($"SH{p1};", "CwReader", ct);

            if (settings.RadioModel == "FTDX3000")
            {
                await _cat.SendCommandAsync("CO00;", "CwReader", ct);
                await _cat.SendCommandAsync("CO02;", "CwReader", ct);
            }
            else
            {
                await _cat.SendCommandAsync($"CO{p1}2;", "CwReader", ct);
                await _cat.SendCommandAsync($"CO{p1}3;", "CwReader", ct);
            }
        }

        private async Task EnsureConnectedAsync(ApplicationSettings settings)
        {
            if (_cat.IsConnected) return;
            await _cat.ConnectAsync(settings.SerialPort, settings.BaudRate);
        }

        private static bool IsCw(string? mode) => mode is "CW-U" or "CW-L";

        /// <summary>The MD digit for the modes Reader Mode can select.</summary>
        private static string? ModeCode(string? mode) => mode switch
        {
            "LSB"     => "1",
            "USB"     => "2",
            "CW-U"    => "3",
            "FM"      => "4",
            "AM"      => "5",
            "RTTY-L"  => "6",
            "CW-L"    => "7",
            "DATA-L"  => "8",
            "RTTY-U"  => "9",
            "DATA-FM" => "A",
            "FM-N"    => "B",
            "DATA-U"  => "C",
            "AM-N"    => "D",
            "PSK"     => "E",
            _         => null,
        };

        private CwReaderModeStatus Describe(string message) => new()
        {
            On            = _saved is not null,
            Message       = message,
            Mode          = _state.ModeA,
            IfWidthCode   = _state.IfWidthA,
            IfWidthHz     = YaesuIfWidth.HzForCode(_state.RadioModel, _state.ModeA, _state.IfWidthA),
            ApfOn         = _state.ApfOnA,
            RestoresMode  = _saved?.Mode,
            RestoresWidth = _saved?.IfWidthCode,
        };
    }

    /// <summary>
    /// What Reader Mode did, and what it will put back. The restore values are
    /// reported because an operator who can see what the button is holding for
    /// them is much more likely to trust it with their filter settings.
    /// </summary>
    public sealed class CwReaderModeStatus
    {
        public bool On { get; init; }
        public string Message { get; init; } = "";
        public string? Mode { get; init; }
        public string? IfWidthCode { get; init; }
        public int? IfWidthHz { get; init; }
        public bool ApfOn { get; init; }
        public string? RestoresMode { get; init; }
        public string? RestoresWidth { get; init; }
    }
}
