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
    /// <para>1. <i>Only from the modes RTTY cannot be copied in at all</i> - see
    /// <see cref="NeedsSwitching"/>. The tuner's own documentation supports AFSK:
    /// in DATA-L, DATA-U, LSB or USB the tones are made by the operator's software
    /// and the radio is a plain SSB transceiver, which is a working arrangement
    /// that this switching would break. So the switch fires from CW, AM, FM and
    /// DATA-FM, and leaves every mode that can actually pass an audio tone pair
    /// where it is.</para>
    ///
    /// <para>2. <i>The filter is only ever widened.</i> Changing to RTTY brings
    /// the radio's own stored RTTY filter width with it, which is whatever that
    /// operator normally uses for RTTY and is better evidence about their band
    /// than any formula. The width is touched only when it is too narrow to have
    /// carried the shift the tuner is set to, and then only up to the floor
    /// <see cref="RttyIfWidth"/> computes. Narrowing is left to the operator.</para>
    ///
    /// <para><b>This is Icom Web Control's service, ported.</b> The structure, the
    /// comments and the status strings are deliberately kept word for word so the
    /// two can be diffed; what differs is this radio, and there are exactly three
    /// places. The mode and the filter are written as Yaesu CAT (MD and SH) rather
    /// than through IWC's <c>IRadioController</c> seam; the saved width is kept as
    /// an SH <i>code</i> rather than in Hz, because that is what this radio speaks
    /// and a code put back is exactly the filter that was there; and
    /// <see cref="PassbandCentreHz"/> answers differently, which is the one
    /// difference that is about physics rather than plumbing.</para>
    /// </summary>
    public sealed class RttyTunerModeService
    {
        /// <summary>
        /// The mode the switch goes to: RTTY normal, the one the UI calls
        /// "RTTY-L".
        ///
        /// Not RTTY-U (RTTY-R). Which of the two is right depends on the station,
        /// and the tuner already has a Reverse control for that; starting from
        /// normal means Reverse means what the operator expects it to mean, and
        /// the mark and shift defaults are the normal-mode ones.
        /// </summary>
        public const string RttyMode = "RTTY-L";

        private readonly ICatClient _cat;

        // Reads go through the multiplexer, not through ICatClient, for the
        // reason CwReaderModeService gives: SendCommandAsync hands the reply
        // back to the caller and never dispatches it, so RadioStateService
        // would not see it and what gets written back afterwards would be the
        // cached value rather than the radio's.
        private readonly CatMultiplexerService _mux;

        private readonly RadioStateService _state;
        private readonly ISettingsService _settings;
        private readonly ILogger<RttyTunerModeService> _logger;

        // One at a time. Ensuring and restoring both read the radio, decide from
        // what came back and write, so two of them interleaved could save this
        // service's own settings over the operator's.
        private readonly SemaphoreSlim _gate = new(1, 1);

        private Saved? _saved;

        // The FSK mode the tuner is operating in and will put back if something
        // else moves the radio out of it - null when the tuner is not holding
        // the mode at all, which is every AFSK session: there the mode is the
        // operator's software's business and nothing here may touch it.
        private string? _heldMode;

        // The mark, shift and speed the filter was last sized for. Every poll of
        // the figure is preceded by a start, and a start that read the mode and
        // the filter width would put two CAT round trips in front of every one
        // of them. Nothing about the radio can need looking at twice for the
        // same signal, so a repeat start asks it nothing.
        private (double Mark, int Shift, double Baud)? _sizedFor;

        public RttyTunerModeService(ICatClient cat,
                                    CatMultiplexerService mux,
                                    RadioStateService state,
                                    ISettingsService settings,
                                    ILogger<RttyTunerModeService> logger)
        {
            _cat = cat;
            _mux = mux;
            _state = state;
            _settings = settings;
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
        /// The FSK mode being held for as long as the tuner runs, or null if
        /// none is. Read without the gate on purpose: it is a single reference
        /// and the only caller is deciding whether a re-assert is worth a task
        /// at all, which <see cref="ReassertAsync"/> then re-checks properly.
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
        /// passband reaches them - see <see cref="PassbandCentreHz"/>.
        /// </param>
        /// <param name="shiftHz">The shift the tuner's filters are set to.</param>
        /// <param name="baud">The speed the tuner's filters are set for.</param>
        public async Task<string?> EnsureAsync(double markHz, int shiftHz, double baud,
                                               CancellationToken ct = default)
        {
            var settings = await _settings.GetSettingsAsync();
            if (!settings.RttyTunerSetMode) return null;

            await _gate.WaitAsync(ct);
            try
            {
                if (!_cat.IsConnected) return null;
                if (_saved is not null && _sizedFor == (markHz, shiftHz, baud)) return null;

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

                    if (NeedsSwitching(mode))
                    {
                        await SetModeAsync(RttyMode, ct);
                        switched = $"Mode set to {RttyMode}.";
                        _logger.LogInformation(
                            "RTTY tuner: mode {From} cannot carry RTTY, set to {To}", mode, RttyMode);
                    }
                }

                // After the mode, always - the width belongs to the mode it was
                // set in, so a width written before a mode change is a width set
                // for the mode the radio is about to leave. The mode change also
                // brings the radio's own stored RTTY width with it, which is the
                // width this then judges.
                var widened = await WidenIfNeededAsync(settings, markHz, shiftHz, baud, ct);
                _sizedFor = (markHz, shiftHz, baud);

                // What to hold. The mode the radio is in now, if it is one of
                // the radio's own FSK modes - which is either the one just
                // written or one it was already in. An AFSK session holds
                // nothing: RTTY-L would be the wrong answer there, and the
                // operator did not ask for it.
                _heldMode = RttyMarkCentre.SidebandForFskMode(_state.ModeA) is not null
                    ? _state.ModeA
                    : null;

                return Join(switched, widened);
            }
            finally { _gate.Release(); }
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
                // filter nobody chose.
                if (int.TryParse(saved.IfWidthCode, out int code))
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
                _logger.LogInformation("RTTY tuner: restored mode {Mode}, IF width code {Code}",
                                       saved.Mode, saved.IfWidthCode ?? "none");
            }
            finally { _gate.Release(); }
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
        /// the operator's own mode back. Nothing is held in an AFSK session: see
        /// <see cref="HeldMode"/>.</para>
        /// </summary>
        public async Task<string?> ReassertAsync(CancellationToken ct = default)
        {
            if (_heldMode is null) return null;
            if (RttyMarkCentre.SidebandForFskMode(_state.ModeA) is not null) return null;

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
                if (RttyMarkCentre.SidebandForFskMode(now) is not null) return null;
                if (!_cat.IsConnected) return null;

                await SetModeAsync(hold, ct);
                _logger.LogInformation(
                    "RTTY tuner: mode went to {From} with the tuner open, held at {To}", now, hold);

                return $"Mode went to {now} - held at {hold}. Close the tuner to leave RTTY.";
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Whether the radio is in a mode that cannot carry an RTTY tone pair at
        /// all - and so the only modes this service will take the radio out of.
        ///
        /// <para>CW, because its filters go down to 50 Hz and an operator who was
        /// just using CW is very likely sitting behind one of them. AM and FM
        /// because their detectors destroy the phase relationship the figure is
        /// drawn from. DATA-FM and the narrow variants for the same reason as FM.
        /// Everything else - RTTY, RTTY-R, SSB, DATA, PSK - either is FSK already
        /// or is a perfectly good way to receive an audio tone pair, and is left
        /// alone.</para>
        ///
        /// <para>Wider than Icom Web Control's list, which has no C4FM and no
        /// narrow FM or AM to name.</para>
        /// </summary>
        public static bool NeedsSwitching(string? mode) =>
            mode is "CW-U" or "CW-L" or "AM" or "AM-N" or "FM" or "FM-N"
                 or "C4FM" or "DATA-FM" or "DATA-FM-N";

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
        /// Where this radio centres its RTTY IF passband, in Hz of audio: at a
        /// fixed point near 1800 Hz, following neither tone.
        ///
        /// <para>Bench-measured on the FTdx101MP on 2026-10-09 and not a guess.
        /// The decisive experiment was the same signal - DDK9, 450 Hz shift, 50
        /// baud - at two audio placements, with the dial moved and nothing else
        /// touched. With mark 2125 / space 2575 the copy survived down to a
        /// 1700 Hz filter and was dead at 1200; with mark 1675 / space 2125 it
        /// survived down to <b>800 Hz</b> and was dead at 600. A factor of two
        /// from the audio placement alone, at identical shift and speed, is the
        /// proof that no formula in shift and baud can be right: both columns fit
        /// one fixed centre, C = 1725-1875. Both survival figures also land
        /// exactly on a rung of this radio's own CW/RTTY ladder (1700 and 800),
        /// which is where the measurement and the table agree.</para>
        ///
        /// <para><b>Measured, not explained.</b> 1800 Hz is suspiciously close to
        /// this radio's own RTTY menu centre if that menu is set to mark 1275 with
        /// an 850 Hz shift, but the menu was not read, so the figure stands on the
        /// measurement alone - and an operator with different RTTY menu settings
        /// may well sit somewhere else. It is a method rather than a constant for
        /// that reason, and because Icom Web Control answers differently: the
        /// IC-7300 MkII centres its RTTY passband on the <i>mark</i>, which is why
        /// <see cref="RttyIfWidth"/> takes the centre as a parameter and assumes
        /// neither behaviour.</para>
        /// </summary>
        private static double PassbandCentreHz(double markHz) => 1800.0;

        private async Task<string?> WidenIfNeededAsync(
            ApplicationSettings settings, double markHz, int shiftHz, double baud,
            CancellationToken ct)
        {
            string mode = _state.ModeA ?? "";
            double centre = PassbandCentreHz(markHz);

            // The worse of the two places the space tone can sit, not the one the
            // current mode puts it in. Reverse is a toggle the operator can flip at
            // any moment, and the mode can change under us too, while this widen
            // runs once at the start of a session; sizing for the nearer placement
            // would mean a filter that silently clips the moment they press
            // Reverse. On this radio the centre is fixed rather than pinned to the
            // mark, so the two placements really are different distances from it
            // and this choice is doing work that the Icom's never has to.
            double worstSpace =
                Math.Abs((markHz + shiftHz) - centre) >= Math.Abs((markHz - shiftHz) - centre)
                    ? markHz + shiftHz
                    : markHz - shiftHz;

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

            if (RttyIfWidth.WidenToHz(current, markHz, worstSpace, baud, centre) is not { } want)
                return null;

            // Should not happen once the width above read in Hz, since both go
            // through the same table - but the mode can move between the two
            // lookups, and a null here would otherwise be a silent no-op.
            if (RungAtOrAbove(settings, mode, want) is not { } rung)
                return $"The IF filter needs to be at least {want} Hz for a {shiftHz} Hz shift, "
                     + "and this radio's filter widths are not known here - set it by hand.";

            string p1 = RadioCapabilities.VfoP1(_state.IsSingleReceiver, "A");
            await _cat.SendCommandAsync(
                CatCommands.FormatIfWidth(settings.RadioModel, p1, rung.Code), "RttyTuner", ct);
            _state.IfWidthA = rung.Code.ToString();

            int got = await ReadWidthBackAsync(settings, mode, ct) ?? rung.Hz;

            _logger.LogInformation(
                "RTTY tuner: IF width {Current} Hz is too narrow for a {Shift} Hz shift at {Baud} baud "
                + "with the mark at {Mark} Hz, widened to {Actual} Hz",
                current, shiftHz, baud, markHz, got);

            // The widest filter the radio has may still not reach, if the pair sits
            // far enough off the centre of the passband. Saying "widened to 500 Hz"
            // and stopping there would read as success while the screen stayed
            // empty, so the operator is told the dial is the thing to move.
            if (!RttyIfWidth.Passes(got, markHz, worstSpace, baud, centre))
                return $"IF width {current} Hz was too narrow for {shiftHz} Hz shift; widened to {got} Hz, "
                     + "which is still not enough for this tone pair - move the dial to bring the tones "
                     + "closer to the middle of the passband.";

            return $"IF width {current} Hz was too narrow for {shiftHz} Hz shift; widened to {got} Hz.";
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
