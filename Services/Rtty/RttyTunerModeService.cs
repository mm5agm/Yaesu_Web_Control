using RadioWebControl.Core.Services.Rtty;
using Yaesu_Web_Control.Models;

namespace Yaesu_Web_Control.Services.Rtty
{
    /// <summary>
    /// Puts the radio into RTTY when the tuner starts, and puts it back when the
    /// tuner stops.
    ///
    /// <para>The case for it came off the bench on 2026-10-08. The tuner opens on
    /// whatever the radio was last doing, and what the radio was last doing is
    /// often CW with a 250 Hz filter - a passband that cannot carry a 170 Hz
    /// shift's keying sidebands, never mind DDK9's 450. The figure then draws a
    /// perfectly convincing ellipse built out of one tone and the skirt of the
    /// other, and the operator has no way to tell that from a correctly tuned
    /// signal. <b>A tuning indicator that lies is worse than no tuning
    /// indicator</b>, so the settings it needs are set rather than hoped for.</para>
    ///
    /// <para><b>The restore is why this lives on the server</b>, for exactly the
    /// reason <see cref="Cw.CwReaderModeService"/> gives: the obvious
    /// implementation is a couple of fetch calls from the dialog, and it works
    /// right up until the operator reloads the page, at which point the record of
    /// what mode they were in has gone with the tab. This must stay a singleton
    /// for the same reason.</para>
    ///
    /// <para><b>Two deliberate restraints</b>, both narrower than the CW
    /// equivalent:</para>
    ///
    /// <para>1. <i>Opening the tuner puts the radio in the operator's RTTY
    /// mode</i> (<see cref="ApplicationSettings.RttyTunerMode"/>) from any other
    /// mode - see <see cref="NeedsSwitching"/>.
    /// Until 2026-10-13 SSB and DATA were left alone for AFSK; Colin asked for
    /// RTTY on open, and an AFSK operator who wants the radio left alone turns
    /// <see cref="ApplicationSettings.RttyTunerSetMode"/> off. Since 2026-10-26
    /// an AFSK operator can choose DATA-L or DATA-U instead, which Bruce VK2RT
    /// asked for: the tuner then puts the radio in DATA and holds it there.</para>
    ///
    /// <para>2. <i>The filter is sized to the signal.</i> The narrowest rung
    /// at or above the floor <see cref="RttyIfWidth"/> computes, wider or
    /// narrower than what was there. Auto opens it to
    /// <see cref="ListenWidthHz"/> while it listens, so no tone is hidden from
    /// the measurement.</para>
    ///
    /// <para><b>This is Icom Web Control's service, ported.</b> The structure, the
    /// comments and the status strings are deliberately kept word for word so the
    /// two can be diffed; what differs is this radio, and there are exactly three
    /// places. The mode and the filter are written as Yaesu CAT (MD and SH) rather
    /// than through IWC's <c>IRadioController</c> seam; the saved width is kept as
    /// an SH <i>code</i> rather than in Hz, because that is what this radio speaks
    /// and a code put back is exactly the filter that was there; and
    /// <see cref="PassbandCentreAsync"/> answers differently, which is the one
    /// difference that is about physics rather than plumbing.</para>
    /// </summary>
    public sealed class RttyTunerModeService
    {
        /// <summary>
        /// The mode the switch goes to unless the operator has chosen another:
        /// RTTY normal, the one the UI calls "RTTY-L".
        ///
        /// Not RTTY-U (RTTY-R). Which of the two is right depends on the station,
        /// and the tuner already has a Reverse control for that; starting from
        /// normal means Reverse means what the operator expects it to mean, and
        /// the mark and shift defaults are the normal-mode ones.
        /// </summary>
        public const string RttyMode = "RTTY-L";

        /// <summary>
        /// The modes an operator can choose for RTTY: the radio's two FSK modes,
        /// and the two DATA modes AFSK software uses.
        /// </summary>
        public static readonly IReadOnlyList<string> Modes = ["RTTY-L", "RTTY-U", "DATA-L", "DATA-U"];

        /// <summary>One of <see cref="Modes"/>, spelled as listed there, or null.</summary>
        public static string? Normalise(string? mode) =>
            Modes.FirstOrDefault(m => string.Equals(m, mode?.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>The mode the tuner switches to and holds.</summary>
        public static string TargetMode(ApplicationSettings settings) =>
            Normalise(settings.RttyTunerMode) ?? RttyMode;

        private static bool IsData(string? mode) =>
            string.Equals(mode, "DATA-L", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "DATA-U", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether <paramref name="mode"/> is the same kind as
        /// <paramref name="target"/>: both FSK, or both DATA. Either sideband
        /// will do, as it always has for FSK: which one is right depends on the
        /// station, and the tuner's Reverse is for that.
        /// </summary>
        public static bool SameKind(string? mode, string target) =>
            IsData(target)
                ? IsData(mode)
                : RttyMarkCentre.SidebandForFskMode(mode) is not null;

        /// <summary>
        /// How wide Auto listens. A filter left narrow from the last signal - or
        /// a CW filter's worth - cannot show Auto a station it has not yet found.
        /// </summary>
        public const int ListenWidthHz = 2400;

        private readonly ICatClient _cat;

        // Reads go through the multiplexer, not through ICatClient, for the
        // reason CwReaderModeService gives: SendCommandAsync hands the reply
        // back to the caller and never dispatches it, so RadioStateService
        // would not see it and what gets written back afterwards would be the
        // cached value rather than the radio's.
        private readonly CatMultiplexerService _mux;

        private readonly RadioStateService _state;
        private readonly ISettingsService _settings;
        private readonly RttyRadioToneService _tones;
        private readonly IBandPlanService _bands;
        private readonly ILogger<RttyTunerModeService> _logger;

        // One at a time. Ensuring and restoring both read the radio, decide from
        // what came back and write, so two of them interleaved could save this
        // service's own settings over the operator's.
        private readonly SemaphoreSlim _gate = new(1, 1);

        private Saved? _saved;

        // The mode the tuner is operating in and will put back if something
        // else moves the radio out of its kind - null when the tuner is not
        // holding the mode at all, which is every session with
        // RttyTunerSetMode off: there the mode is the operator's business.
        private string? _heldMode;

        // The mark, shift and speed the filter was last sized for. Every poll of
        // the figure is preceded by a start, and a start that read the mode and
        // the filter width would put two CAT round trips in front of every one
        // of them. Nothing about the radio can need looking at twice for the
        // same signal, so a repeat start asks it nothing.
        private (double Mark, int Shift, double Baud, bool Reverse)? _sizedFor;

        // The operator set the IF width by hand while the tuner held the
        // radio. Bruce VK2RT, Discussion #257: the WIDTH he chose on RTTY
        // Contest kept being undone, by the sizing on the next start and by
        // the restore on the way out. From then until the tuner closes the
        // width is his, and closing leaves it where he put it.
        private bool _widthByHand;

        // The tones the tuner last started on, so that AUTO on WIDTH can size
        // the filter again there and then (ForgetWidthAsync). Unlike
        // _sizedFor, nothing else clears it.
        private (double Mark, int Shift, double Baud, bool Reverse)? _lastTones;

        public RttyTunerModeService(ICatClient cat,
                                    CatMultiplexerService mux,
                                    RadioStateService state,
                                    ISettingsService settings,
                                    RttyRadioToneService tones,
                                    IBandPlanService bands,
                                    ILogger<RttyTunerModeService> logger)
        {
            _cat = cat;
            _mux = mux;
            _state = state;
            _settings = settings;
            _tones = tones;
            _bands = bands;
            _logger = logger;
        }

        /// <summary>
        /// What was there before. The width is the radio's own SH code, not a
        /// bandwidth: putting a code back restores exactly the filter the
        /// operator had, where a figure in Hz would have to be matched to the
        /// nearest rung on the way out and could land one rung away from where
        /// it started.
        /// </summary>
        private sealed record Saved(string? Mode, string? IfWidthCode);

        public bool IsOn => _saved is not null;

        /// <summary>
        /// The mode being held for as long as the tuner runs, or null if
        /// none is. Read without the gate on purpose: it is a single reference
        /// and its callers only decide whether a re-assert is worth a task at
        /// all, which <see cref="ReassertAsync"/> then re-checks properly, or
        /// whether Auto's wrong-mode note applies.
        /// </summary>
        public string? HeldMode => _heldMode;

        /// <summary>
        /// Make sure the radio can carry this signal, saving what was there the
        /// first time. Returns a line for the tuner's status bar when something
        /// was changed, null when nothing was.
        ///
        /// <para>Called on every start, including a re-tone, because a re-tone can
        /// change the shift - an operator moving from a 170 Hz amateur signal to
        /// an 850 Hz aviation one needs the filter looked at again, and the mode
        /// not looked at again.</para>
        /// </summary>
        /// <param name="markHz">
        /// The mark tone, in Hz of audio. Needed as well as the shift because
        /// where the tones sit in the audio is what decides whether this radio's
        /// passband reaches them - see <see cref="PassbandCentreAsync"/>.
        /// </param>
        /// <param name="shiftHz">The shift the tuner's filters are set to.</param>
        /// <param name="baud">The speed the tuner's filters are set for.</param>
        /// <param name="reverse">
        /// Which side of the mark the space sits on. Needed because Auto centres
        /// the pair, so the mark is off-centre by half a shift on a known side;
        /// sizing for the far placement would be wider by a whole shift.
        /// </param>
        public async Task<string?> EnsureAsync(double markHz, int shiftHz, double baud, bool reverse,
                                               CancellationToken ct = default)
        {
            var settings = await _settings.GetSettingsAsync();
            if (!settings.RttyTunerSetMode) return null;

            await _gate.WaitAsync(ct);
            try
            {
                if (!_cat.IsConnected) return null;
                if (_saved is not null && _sizedFor == (markHz, shiftHz, baud, reverse)) return null;

                string target = TargetMode(settings);
                string? switched = null;
                if (_saved is null)
                {
                    // Read the radio rather than trusting the cached state. On a
                    // cold start RadioStateService still holds the mode from the
                    // last session's saved state - the tuner saw "LSB" there
                    // while the radio was in RTTY-L - and the width is not polled
                    // either. A value that is a second or two stale here is not a
                    // stale display: it is what gets written back afterwards.
                    await ReadCurrentAsync(ct);
                    string mode = _state.ModeA ?? "";
                    string code = _state.IfWidthA;

                    _saved = new Saved(mode, code);
                    _logger.LogInformation(
                        "RTTY tuner: saving mode {Mode}, IF width code {Code} ({Hz})",
                        mode, string.IsNullOrWhiteSpace(code) ? "unknown" : code,
                        WidthHz(settings, mode, code) is { } hz ? hz + " Hz" : "unknown");

                    if (NeedsSwitching(mode, target))
                    {
                        await SetModeAsync(target, ct);
                        switched = $"Mode set to {target}.";
                        _logger.LogInformation(
                            "RTTY tuner: mode {From} set to {To}", mode, target);
                    }
                }

                // After the mode, always - the width belongs to the mode it was
                // set in, so a width written before a mode change is a width set
                // for the mode the radio is about to leave. The mode change also
                // brings the radio's own stored RTTY width with it, which is the
                // width this then judges.
                // A width the operator chose for this mode is used instead of
                // the one worked out, and a width chosen this session is left
                // alone altogether.
                // Failing that, the width the operator's RTTY setup gives the band.
                string? sized = null;
                if (KeptWidth(settings, _state.ModeA, BandNow()) is { } kept)
                    sized = await PutKeptWidthAsync(settings, kept, ct);
                else if (!_widthByHand && SetupWidth(settings) is { } setup)
                    sized = await PutKeptWidthAsync(settings, setup, ct, fromSetup: true);
                else if (!_widthByHand)
                    sized = await SizeFilterAsync(settings, markHz, shiftHz, baud, reverse, ct);
                _sizedFor = (markHz, shiftHz, baud, reverse);
                _lastTones = _sizedFor;

                // What to hold. The mode the radio is in now, if it is the
                // kind the operator chose - which is either the one just
                // written or one it was already in. Anything else, which only
                // a mode the switch could not write leaves, holds nothing.
                _heldMode = SameKind(_state.ModeA, target) ? _state.ModeA : null;

                return Join(switched, sized);
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Open the filter to <see cref="ListenWidthHz"/> for Auto's listen.
        /// Returns the SH code it was at, for <see cref="EndListeningAsync"/>, or
        /// null when nothing was changed - the tuner is not running, the radio is
        /// not in RTTY or DATA, or the filter is already wide enough.
        /// </summary>
        public async Task<string?> BeginListeningAsync(CancellationToken ct = default)
        {
            if (_saved is null) return null;
            await _gate.WaitAsync(ct);
            try
            {
                if (_saved is null || !_cat.IsConnected) return null;
                string mode = _state.ModeA ?? "";
                if (RttyMarkCentre.SidebandForMode(mode) is null) return null;

                var settings = await _settings.GetSettingsAsync();
                string? was = _state.IfWidthA;
                if (WidthHz(settings, mode, was) is not { } current || current >= ListenWidthHz) return null;
                if (RungAtOrAbove(settings, mode, ListenWidthHz) is not { } rung || rung.Hz <= current) return null;

                string p1 = RadioCapabilities.VfoP1(_state.IsSingleReceiver, "A");
                await _cat.SendCommandAsync(
                    CatCommands.FormatIfWidth(settings.RadioModel, p1, rung.Code), "RttyTuner", ct);
                _state.IfWidthA = rung.Code.ToString();
                int got = await ReadWidthBackAsync(settings, mode, ct) ?? rung.Hz;
                _sizedFor = null;
                _logger.LogInformation("RTTY Auto: IF width {From} Hz opened to {To} Hz to listen",
                                       current, got);
                return was;
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Put back the width <see cref="BeginListeningAsync"/> replaced, when
        /// Auto found nothing it could size a filter for.
        /// </summary>
        public async Task EndListeningAsync(string code, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (_saved is null || !_cat.IsConnected || !int.TryParse(code, out int c)) return;
                var settings = await _settings.GetSettingsAsync();
                string p1 = RadioCapabilities.VfoP1(_state.IsSingleReceiver, "A");
                await _cat.SendCommandAsync(
                    CatCommands.FormatIfWidth(settings.RadioModel, p1, c), "RttyTuner", ct);
                _state.IfWidthA = code;
                _sizedFor = null;
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Where Auto should put the middle of the tone pair: this radio's
        /// passband centre, or null when the tuner is not holding an FSK or DATA
        /// mode. In DATA that is the model's DATA carrier point, not a menu read.
        /// Also lets the next start size the filter afresh for the moved pair.
        /// </summary>
        public async Task<double?> CentreForAutoAsync(CancellationToken ct = default)
        {
            string? mode = _state.ModeA;
            if (_saved is null || RttyMarkCentre.SidebandForMode(mode) is null) return null;
            _sizedFor = null;
            return await PassbandCentreAsync(mode, ct);
        }

        /// <summary>
        /// Put back the mode and width that were there before. Restoring when
        /// nothing was saved does nothing, deliberately: the tuner calls this
        /// whenever it stops, and stopping a tuner that never changed the
        /// operator's radio must not change it now.
        /// </summary>
        public async Task RestoreAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var saved = _saved;
                if (saved is null) return;
                if (!_cat.IsConnected)
                {
                    // Left saved on purpose. A radio that is not there cannot be
                    // put back, and dropping the record would mean it never is.
                    _logger.LogInformation(
                        "RTTY tuner: radio not connected, mode {Mode} still owed", saved.Mode);
                    return;
                }

                var settings = await _settings.GetSettingsAsync();

                if (!string.IsNullOrWhiteSpace(saved.Mode))
                    await SetModeAsync(saved.Mode, ct);

                // Width after mode, and only when it was read: a missing code is
                // "could not read it", and writing a guess would be inventing a
                // filter nobody chose. Not when the operator set one by hand
                // while the tuner was open: that one is theirs to keep.
                if (!_widthByHand && int.TryParse(saved.IfWidthCode, out int code))
                {
                    string p1 = RadioCapabilities.VfoP1(_state.IsSingleReceiver, "A");
                    await _cat.SendCommandAsync(
                        CatCommands.FormatIfWidth(settings.RadioModel, p1, code), "RttyTuner", ct);
                    _state.IfWidthA = saved.IfWidthCode!;
                }

                // Cleared last. If a write threw half way through, the operator
                // still has a tuner that will try the restore again next time,
                // which is more use than a service that believes it already has.
                _saved = null;
                _sizedFor = null;
                _heldMode = null;
                bool byHand = _widthByHand;
                _widthByHand = false;
                _logger.LogInformation("RTTY tuner: restored mode {Mode}, IF width code {Code}",
                                       saved.Mode, byHand ? "left as set by hand" : saved.IfWidthCode ?? "none");
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// The operator has set VFO A's IF width by hand, to the SH code
        /// <paramref name="code"/>. While MAIN is in the RTTY mode (the tuner's
        /// while it holds the radio, the one chosen in Settings otherwise) that
        /// width is kept for the band and mode it was set in, and used each time
        /// the tuner starts there and after each band key (BandChangedAsync),
        /// instead of the width the tuner would work out. Kept for the band,
        /// not the mode, and with the tuner closed too, since Bruce VK2RT, #250
        /// 2026-11-01: a width set on one band followed him to every other. In
        /// any other mode it means nothing here.
        /// </summary>
        public async Task WidthSetByHandAsync(string code, CancellationToken ct = default)
        {
            if (!int.TryParse(code, out _)) return;
            string mode = _state.ModeA ?? "";
            var settings = await _settings.GetSettingsAsync();
            if (!SameKind(mode, _saved is null ? TargetMode(settings) : _heldMode ?? "")) return;

            if (_saved is not null) _widthByHand = true;
            string band = BandNow();
            settings.RttyTunerWidths[WidthKey(settings, mode, band)] = code;
            await _settings.SaveSettingsAsync(settings);
            _logger.LogInformation("RTTY tuner: IF width code {Code} set by hand in {Mode} on {Band}, kept", code, mode, band);
        }

        /// <summary>
        /// The mode MAIN is in, and the width kept for it as an SH code - null
        /// when the tuner works the width out itself. For WIDTH's AUTO key.
        /// </summary>
        public async Task<(string Mode, string? Code)> KeptWidthNowAsync()
        {
            var settings = await _settings.GetSettingsAsync();
            string mode = _state.ModeA ?? "";
            return (mode, KeptWidth(settings, mode, BandNow()));
        }

        /// <summary>
        /// AUTO on WIDTH: forget the width kept for MAIN's mode on this band, so the
        /// band goes back to where it starts: the width an SH line in the
        /// operator's RTTY setup gives it, put on now (Bruce VK2RT, #250
        /// 2026-11-01: AUTO gave 300 Hz where his setup has 500 Hz), or with
        /// none, the tuner works the width out again, and if the tuner is open,
        /// sizes the filter now. Closing the tuner then puts back the width from
        /// before it opened, as it did before a width was set by hand. Returns a
        /// line for the status bar.
        /// </summary>
        public async Task<string> ForgetWidthAsync(CancellationToken ct = default)
        {
            var settings = await _settings.GetSettingsAsync();
            await _gate.WaitAsync(ct);
            try
            {
                string mode = _state.ModeA ?? "";
                if (settings.RttyTunerWidths.Remove(WidthKey(settings, mode, BandNow())))
                    await _settings.SaveSettingsAsync(settings);
                _widthByHand = false;
                _sizedFor = null;
                _logger.LogInformation("RTTY tuner: IF width in {Mode} back to automatic", mode);

                if (SetupWidth(settings) is { } setup)
                {
                    if (!_cat.IsConnected || !SameKind(mode, _saved is null ? TargetMode(settings) : _heldMode ?? ""))
                        return "WIDTH is back to your RTTY setup's width for this band.";
                    var put = await PutKeptWidthAsync(settings, setup, ct, force: true, fromSetup: true);
                    _sizedFor = _lastTones;
                    return put ?? "WIDTH is back to your RTTY setup's width for this band.";
                }

                if (_saved is null || _lastTones is not { } t || !_cat.IsConnected)
                    return "WIDTH is automatic: the tuner works it out when it starts.";
                var sized = await SizeFilterAsync(settings, t.Mark, t.Shift, t.Baud, t.Reverse, ct);
                _sizedFor = t;
                return sized ?? "WIDTH is automatic, and the width it has is right for the signal.";
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// After a band key, and after the operator's RTTY setup if it went:
        /// the width kept for the new band, if one was set there by hand. If
        /// none was, an SH line in the setup has the say - sent with it
        /// (<paramref name="setupSetWidth"/>), or put on here when the setup is
        /// not sent on a band key - and failing that the tuner, when it is open, sizes the
        /// filter as it does on starting. Bruce VK2RT, #250 2026-11-01: a width
        /// set on a band was lost on the way back to it, because nothing put
        /// it back after a band change. Returns a line for the status bar.
        /// </summary>
        public async Task<string?> BandChangedAsync(bool setupSetWidth, CancellationToken ct = default)
        {
            var settings = await _settings.GetSettingsAsync();
            await _gate.WaitAsync(ct);
            try
            {
                if (!_cat.IsConnected) return null;
                string mode = _state.ModeA ?? "";
                if (!SameKind(mode, TargetMode(settings))) return null;
                if (KeptWidth(settings, mode, BandNow()) is { } kept)
                {
                    // The band key or the setup may have moved the width since
                    // it was last read, so it is always written.
                    return await PutKeptWidthAsync(settings, kept, ct, force: true);
                }
                if (setupSetWidth) return null;
                // A setup that is not sent on a band key still says where
                // the band starts.
                if (SetupWidth(settings) is { } setup)
                    return await PutKeptWidthAsync(settings, setup, ct, force: true, fromSetup: true);
                if (_saved is null || _lastTones is not { } t) return null;
                var sized = await SizeFilterAsync(settings, t.Mark, t.Shift, t.Baud, t.Reverse, ct);
                _sizedFor = t;
                return sized;
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// CAT SET: forget the width kept for every band, so each goes back to
        /// the setup's width, or the tuner's own, until one is set by hand
        /// again (Bruce VK2RT, #250 2026-11-01).
        /// </summary>
        public async Task ForgetAllWidthsAsync()
        {
            var settings = await _settings.GetSettingsAsync();
            string prefix = settings.RadioModel + "|";
            var keys = settings.RttyTunerWidths.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (keys.Count == 0) return;
            foreach (var k in keys) settings.RttyTunerWidths.Remove(k);
            await _settings.SaveSettingsAsync(settings);
            _logger.LogInformation("RTTY tuner: kept IF widths forgotten for every band ({Count})", keys.Count);
        }

        private string BandNow() => _bands.BandForFrequency(_state.FrequencyA);

        private static string WidthKey(ApplicationSettings settings, string? mode, string band)
            => $"{settings.RadioModel}|{mode}|{band}";

        internal static string? KeptWidth(ApplicationSettings settings, string? mode, string band)
            => settings.RttyTunerWidths.TryGetValue(WidthKey(settings, mode, band), out var code)
               && int.TryParse(code, out _) ? code : null;

        // The width the operator's RTTY setup gives the band MAIN is on, as an
        // SH code, or null.
        private string? SetupWidth(ApplicationSettings settings)
            => RttySetupCat.WidthCode(settings.RttySetupCat, BandNow(), settings.RadioModel,
                                      RadioCapabilities.VfoP1(_state.IsSingleReceiver, "A"));

        private async Task<string?> PutKeptWidthAsync(ApplicationSettings settings, string code, CancellationToken ct,
                                                      bool force = false, bool fromSetup = false)
        {
            if (!force && _state.IfWidthA == code) return null;
            string p1 = RadioCapabilities.VfoP1(_state.IsSingleReceiver, "A");
            await _cat.SendCommandAsync(
                CatCommands.FormatIfWidth(settings.RadioModel, p1, int.Parse(code)), "RttyTuner", ct);
            _state.IfWidthA = code;
            _logger.LogInformation("RTTY tuner: IF width code {Code} put back, {Why}", code,
                                   fromSetup ? "from the RTTY setup" : "as set by hand");
            return WidthHz(settings, _state.ModeA, code) is { } hz
                ? (fromSetup ? $"IF filter {hz} Hz, from your RTTY setup." : $"IF filter {hz} Hz, as you set it.")
                : null;
        }

        /// <summary>
        /// Put the held mode back when something else has moved the radio out of
        /// it while the tuner is running. Returns a line for the status bar when
        /// it had to act, null when there was nothing to do.
        ///
        /// <para>The operator's own front panel is the obvious cause, but not the
        /// common one. A band change recalls that band's stacking register, which
        /// carries the mode the band was last used in, so simply moving from 30 m
        /// to 20 m can land the radio in CW with nobody having asked for it. The
        /// page's mode guard then closes the panel two seconds later and the stop
        /// restores the pre-tuner mode, so an incidental mode change ends the
        /// session - which is what the bench reported on 2026-10-09.</para>
        ///
        /// <para><b>While the tuner is open the mode is the tuner's.</b> Leaving
        /// RTTY is done by closing it, which is also the only thing that puts
        /// the operator's own mode back. A move within the kind - RTTY to RTTY-R,
        /// DATA-L to DATA-U - is left alone: see <see cref="SameKind"/>.</para>
        /// </summary>
        public async Task<string?> ReassertAsync(CancellationToken ct = default)
        {
            if (_heldMode is not { } held || SameKind(_state.ModeA, held)) return null;

            await _gate.WaitAsync(ct);
            try
            {
                // Re-read everything inside the gate. A restore may have run
                // while this was waiting for it, in which case the mode is
                // deliberately not RTTY any more and writing one back would
                // undo the operator's own settings a moment after returning
                // them.
                if (_heldMode is not { } hold) return null;
                var now = _state.ModeA;
                if (SameKind(now, hold)) return null;
                if (!_cat.IsConnected) return null;

                await SetModeAsync(hold, ct);
                _logger.LogInformation(
                    "RTTY tuner: mode went to {From} with the tuner open, held at {To}", now, hold);

                return $"Mode went to {now} - held at {hold}. Close the tuner to leave RTTY.";
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// The operator has picked <paramref name="mode"/> for RTTY, and is about
        /// to put the radio in it. While the tuner is holding a mode, the hold
        /// moves to the new one - otherwise the change would be undone as soon
        /// as the radio reported it - and the filter is sized again on the next
        /// start, because it belongs to the mode it was set in.
        /// </summary>
        public async Task ChooseAsync(string mode, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (_heldMode is null) return;
                _heldMode = mode;
                _sizedFor = null;
                _logger.LogInformation("RTTY tuner: operator chose {Mode}, now held", mode);
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Whether the tuner needs to change the radio's mode to
        /// <paramref name="target"/> when it starts: whenever it is not that
        /// mode. Until 2026-10-31 the other sideband of the same kind was left
        /// alone (<see cref="SameKind"/>), from before the operator could choose
        /// the sideband; Bruce VK2RT, in DATA-L, came back from the Radio panel
        /// in DATA-U and it stayed there (#250). While the tuner runs, the hold
        /// still accepts either sideband.
        ///
        /// <para>For RTTY that is every mode but RTTY and RTTY-R: CW, because its
        /// filters go down to 50 Hz and an operator who was just using CW is very
        /// likely sitting behind one of them; AM and FM because their detectors
        /// destroy the phase relationship the figure is drawn from; and SSB and
        /// DATA since 2026-10-13, when Colin asked for RTTY on open. For DATA it
        /// is every mode but DATA-L and DATA-U.</para>
        /// </summary>
        public static bool NeedsSwitching(string? mode, string target) =>
            !string.Equals(mode?.Trim(), target, StringComparison.OrdinalIgnoreCase);

        // ---- the radio -----------------------------------------------------

        /// <summary>The MD digit for the modes this service writes.</summary>
        private static string? ModeCode(string? mode) => mode switch
        {
            "LSB"       => "1",
            "USB"       => "2",
            "CW-U"      => "3",
            "FM"        => "4",
            "AM"        => "5",
            "RTTY-L"    => "6",
            "CW-L"      => "7",
            "DATA-L"    => "8",
            "RTTY-U"    => "9",
            "DATA-FM"   => "A",
            "FM-N"      => "B",
            "DATA-U"    => "C",
            "AM-N"      => "D",
            "PSK"       => "E",
            "DATA-FM-N" => "F",
            _           => null,
        };

        private async Task SetModeAsync(string? mode, CancellationToken ct)
        {
            if (ModeCode(mode) is not { } code) return;
            await _cat.SendCommandAsync($"MD{RadioCapabilities.ModeP1("A")}{code};", "RttyTuner", ct);
            _state.ModeA = mode;
        }

        /// <summary>
        /// Ask the radio for the mode and the filter width, and let the
        /// dispatcher put both into <see cref="RadioStateService"/>.
        /// </summary>
        private async Task ReadCurrentAsync(CancellationToken ct)
        {
            string p1 = RadioCapabilities.VfoP1(_state.IsSingleReceiver, "A");
            await _mux.SendCommandAndDispatchAsync(
                $"MD{RadioCapabilities.ModeP1("A")};", "RttyTuner", ct);
            await _mux.SendCommandAndDispatchAsync($"SH{p1};", "RttyTuner", ct);
        }

        private static int? WidthHz(ApplicationSettings settings, string? mode, string? code)
            => YaesuIfWidth.HzForCode(settings.RadioModel, mode, code);

        // ---- the filter ----------------------------------------------------

        /// <summary>
        /// Where this radio centres its receive passband, in Hz of audio, plus
        /// the IF SHIFT.
        ///
        /// <para><b>In RTTY it is the radio's own RTTY menu</b>: MARK FREQUENCY
        /// plus half the SHIFT FREQUENCY in RTTY-L - the middle of the pair the
        /// radio itself would expect. Measured on the FTdx101MP on 2026-10-13
        /// against DDK9 at 600 Hz, receive only. With the menu at 2125/170 the
        /// pair balanced with its middle on <b>2208 Hz</b> (2125 + 85 = 2210)
        /// and was 20 dB and more down when centred on 1485; then, with the dial
        /// untouched, the menu alone set to 1275/425 swapped the two - the pair
        /// on 1483 balanced within 0.5 dB and the one on 2208 was lost. The menu
        /// is the cause, not the band.</para>
        ///
        /// <para>That is what the 2026-10-10 noise sweep's 1485 was: 1275 +
        /// 425/2 = 1487, the menu that evening, read as a constant of the radio.
        /// It did hold for DATA-U and USB as well, which have no RTTY menu - so
        /// outside RTTY the model figure below still stands - and it tracked IF
        /// SHIFT one for one there (1482 / 1781 / 1201 for 0 / +300 / -300),
        /// which is why the shift is still added. That IF SHIFT moves the RTTY
        /// filter the same way is assumed, not measured.</para>
        ///
        /// <para>RTTY-U is taken as the mirror, mark minus half the shift, and
        /// is not measured. Nor is the menu's POLARITY item: the 2026-10-13
        /// proof ran with it as the radio had it.</para>
        ///
        /// <para>The IC-7300 follows the same rule (Icom Web Control, 2026-10-13),
        /// so the two radios no longer disagree. Read live rather than cached:
        /// the operator can change that menu at the front panel, and the tuner's
        /// own "To radio" button writes it.</para>
        /// </summary>
        private async Task<double> PassbandCentreAsync(string? mode, CancellationToken ct)
        {
            if (RttyMarkCentre.SidebandForFskMode(mode) is not null
                && await _tones.ReadAsync(ct) is { } menu)
            {
                double half = menu.ShiftHz / 2.0;
                double c = string.Equals(mode, "RTTY-U", StringComparison.OrdinalIgnoreCase)
                    ? menu.MarkHz - half
                    : menu.MarkHz + half;
                return c + _state.IfShiftA;
            }
            return ModelCentreHz(_state.RadioModel) + _state.IfShiftA;
        }

        /// <summary>
        /// The audio centre outside RTTY, with IF SHIFT at zero, or in RTTY when
        /// the menu could not be read. Only the FTdx101MP has been swept (DATA-U
        /// and USB, 2026-10-10); anything else gets Yaesu's published DATA
        /// carrier point.
        /// </summary>
        private static double ModelCentreHz(string? model) => model switch
        {
            "FTdx101MP" or "FTdx101D" => 1485.0,   // swept 2026-10-10, DATA-U/USB
            _                         => 1500.0,
        };

        private async Task<string?> SizeFilterAsync(
            ApplicationSettings settings, double markHz, int shiftHz, double baud, bool reverse,
            CancellationToken ct)
        {
            string mode = _state.ModeA ?? "";
            double centre = await PassbandCentreAsync(mode, ct);

            // The space where this mode and Reverse put it. This used to size for
            // the worse of the two placements, so that flipping Reverse could not
            // clip; it no longer needs to, because a Reverse flip is a start with
            // new figures and is sized again (it is part of _sizedFor). And it
            // must not: with Auto centring the pair, the far placement is a whole
            // shift further out and the filter would come out a shift too wide.
            double worstSpace = RttyTunerService.TonesFor(mode, markHz, shiftHz, reverse).SpaceHz;

            // The width this radio reports is an SH code, so turning it into Hz
            // needs the table - and that is the step IWC does not have, where the
            // radio answers in Hz directly. No Hz means no judgement to make:
            // an unknown model, or a mode with no IF width at all. Leaving the
            // filter alone is the honest response - a guessed SH code would set a
            // bandwidth nobody chose, on a rig nobody here has tested against -
            // but the operator is still told, because their figure is about to be
            // drawn through a filter that may not carry the signal.
            if (WidthHz(settings, mode, _state.IfWidthA) is not { } current)
            {
                if (RttyIfWidth.MinimumHz(markHz, worstSpace, baud, centre) is not { } floor)
                    return null;

                _logger.LogWarning(
                    "RTTY tuner: no IF width in Hz for {Model} in {Mode} (code {Code}), "
                    + "leaving the filter alone; it needs at least {Floor} Hz",
                    settings.RadioModel, mode, _state.IfWidthA, floor);

                return $"The IF filter needs to be at least {floor} Hz for a {shiftHz} Hz shift, "
                     + "and this radio's filter widths are not known here - set it by hand.";
            }

            // The floor, then the narrowest rung that clears it - and that rung
            // is written whether it is wider OR narrower than what the radio is
            // using. This was widen-only until 2026-10-11, on the honest grounds
            // that no evidence said narrow beat wide: the live sweep that tried to
            // show it scored the same 2400 Hz rung 90.9% and then 53.3% six
            // minutes later, so the band was moving more than the filter.
            //
            // The evidence now exists, and it came from ten minutes of one
            // recorded contest station decoded many times with a simulated filter
            // (RttyBench --sweep width, five 120 s captures pooled, scored on
            // known tokens):
            //
            //   added noise   as recorded, 2400 Hz   best rung      gain
            //   none          69.4% (129 known)      350 -> 71.1%   +1.7
            //   12 dB         68.1% (128)            300 -> 71.5%   +3.4
            //    6 dB         67.0% (124)            300 -> 70.8%   +3.8
            //    3 dB         66.5% (119)            300 -> 68.9%   +2.4
            //    0 dB         61.6% (106 known)      250 -> 66.7%   +5.1, +14 tokens
            //
            // Two things in that table matter more than the sizes. The benefit
            // GROWS as the signal weakens, which is the physical prediction and
            // not something a fluke does in order; and framing errors fall at
            // every single noise level, 29.5-30.6% wide against 26.7-27.8%
            // narrow. The floor for that pair computed to 269 Hz - rung 300 - and
            // 300 is best or joint-best at three of the five levels. The formula
            // picks the right rung, so there is nothing here to tune, only a
            // direction to stop refusing.
            //
            // The simulated filter is steeper than this radio's, so the sizes are
            // an upper bound rather than a promise. The direction is not: on the
            // air the same morning, with the pair centred, every rung down to
            // 250 Hz copied.
            if (RttyIfWidth.MinimumHz(markHz, worstSpace, baud, centre) is not { } want)
                return null;

            // Should not happen once the width above read in Hz, since both go
            // through the same table - but the mode can move between the two
            // lookups, and a null here would otherwise be a silent no-op.
            if (RungAtOrAbove(settings, mode, want) is not { } rung)
                return $"The IF filter needs to be at least {want} Hz for a {shiftHz} Hz shift, "
                     + "and this radio's filter widths are not known here - set it by hand.";

            // Already on the right rung: write nothing. A no-op write still costs
            // a CI-V exchange on a bus the scope and the meters are sharing, and
            // still has to be undone on the way out.
            if (rung.Hz == current) return null;

            bool narrowing = rung.Hz < current;

            string p1 = RadioCapabilities.VfoP1(_state.IsSingleReceiver, "A");
            await _cat.SendCommandAsync(
                CatCommands.FormatIfWidth(settings.RadioModel, p1, rung.Code), "RttyTuner", ct);
            _state.IfWidthA = rung.Code.ToString();

            int got = await ReadWidthBackAsync(settings, mode, ct) ?? rung.Hz;

            _logger.LogInformation(
                "RTTY tuner: IF width {Current} Hz {Verb} for a {Shift} Hz shift at {Baud} baud "
                + "with the mark at {Mark} Hz and the passband centred on {Centre} Hz, {Verb2} to {Actual} Hz",
                current, narrowing ? "passes but is wider than it needs to be" : "is too narrow",
                shiftHz, baud, markHz, centre, narrowing ? "narrowed" : "widened", got);

            // The widest filter the radio has may still not reach, if the pair sits
            // far enough off the centre of the passband. Saying "widened to 500 Hz"
            // and stopping there would read as success while the screen stayed
            // empty, so the operator is told the dial is the thing to move.
            if (!RttyIfWidth.Passes(got, markHz, worstSpace, baud, centre))
                return $"IF width {current} Hz was too narrow for {shiftHz} Hz shift; widened to {got} Hz, "
                     + "which is still not enough for this tone pair - move the dial to bring the tones "
                     + "closer to the middle of the passband.";

            return narrowing
                ? $"IF width narrowed from {current} Hz to {got} Hz, the narrowest that carries a "
                  + $"{shiftHz} Hz shift at {baud:N2} baud here."
                : $"IF width {current} Hz was too narrow for {shiftHz} Hz shift; widened to {got} Hz.";
        }

        /// <summary>
        /// The narrowest filter this radio offers that is at least
        /// <paramref name="wantedHz"/> wide, or the widest it has if none reaches -
        /// with null reserved for "no table", which is a different answer and must
        /// leave the filter alone.
        ///
        /// <para><b>Not <see cref="YaesuIfWidth.CodeForHz"/>.</b> That returns the
        /// <i>nearest</i> width, which is right for Reader Mode - asked for 250 Hz
        /// on a radio whose CW set starts at 500, the nearest is the only sensible
        /// answer. It is wrong here: a floor is a floor, and the nearest rung to a
        /// 1250 Hz floor on the FTdx101MP is 1200, which is below it. That would
        /// write a filter, report a widen, and still clip the signal.</para>
        ///
        /// <para>Built by asking <see cref="YaesuIfWidth.HzFor"/> about each code
        /// rather than reading the table directly, so this knows nothing about any
        /// particular radio's ladder and a model added there needs no change
        /// here.</para>
        /// </summary>
        private static (int Code, int Hz)? RungAtOrAbove(
            ApplicationSettings settings, string? mode, int wantedHz)
        {
            (int Code, int Hz)? best = null;    // narrowest at or above the floor
            (int Code, int Hz)? widest = null;  // fallback when nothing reaches

            // Code 0 is the radio's mode-dependent default and resolves to no
            // known width, so the scan starts at 1. The upper bound is generous:
            // the longest ladder in the table today ends at 21.
            for (int code = 1; code <= 31; code++)
            {
                if (YaesuIfWidth.HzFor(settings.RadioModel, mode, code) is not { } hz || hz <= 0)
                    continue;

                if (widest is null || hz > widest.Value.Hz) widest = (code, hz);
                if (hz >= wantedHz && (best is null || hz < best.Value.Hz)) best = (code, hz);
            }

            return best ?? widest;
        }

        /// <summary>
        /// Read the width back rather than recording what was asked for. The radio
        /// is the authority on what it did with an SH code, and the operator is
        /// about to be shown the number - showing them the request instead of the
        /// result would be a quiet lie the moment the two differ.
        /// </summary>
        private async Task<int?> ReadWidthBackAsync(
            ApplicationSettings settings, string? mode, CancellationToken ct)
        {
            string p1 = RadioCapabilities.VfoP1(_state.IsSingleReceiver, "A");
            await _mux.SendCommandAndDispatchAsync($"SH{p1};", "RttyTuner", ct);
            return WidthHz(settings, mode, _state.IfWidthA);
        }

        private static string? Join(string? a, string? b) =>
            (a, b) switch
            {
                (null, null) => null,
                (null, _)    => b,
                (_, null)    => a,
                _            => a + " " + b,
            };
    }
}
