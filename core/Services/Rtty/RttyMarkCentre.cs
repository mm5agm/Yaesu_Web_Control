using System;
using RadioWebControl.Core.Services.Cw;

namespace RadioWebControl.Core.Services.Rtty
{
    /// <summary>
    /// Where the VFO has to move to put a measured mark tone onto the mark the
    /// tuner's filters are sitting on.
    ///
    /// <para>The case for this came off the bench on 2026-10-08. The analyser
    /// measured a 450 Hz shift correctly on eight presses out of eight, and the
    /// speed on only three of them - 105.17 baud on one press, 46.78 on another,
    /// for a station sending a published 50. The dial was some 350 Hz away from
    /// where the tones belonged, which is why: a tone pair sitting off-centre in
    /// the IF arrives with one tone attenuated, and keying whose two halves have
    /// unequal amplitude is keying whose run lengths cannot be measured. <b>The
    /// tone separation is robust to mistuning and the speed is not</b>, so the
    /// useful thing to do with a confident tone measurement is to spend it on
    /// fixing the tuning - after which everything downstream of the tuning gets
    /// better without being asked to.</para>
    ///
    /// <para><b>The sum is <see cref="CwZeroIn"/>'s</b>, called rather than
    /// copied. "Bring a measured audio tone onto a wanted audio tone, given which
    /// way the receiver inverts" is one question with one answer, and the sign is
    /// the only part of it that is easy to get wrong - which is precisely why that
    /// class says it exists so that both applications agree about the sign. What
    /// lives here is the RTTY reasoning about <i>what to pass it</i> and <i>when
    /// the answer is worth acting on</i>.</para>
    ///
    /// <para><b>Which way the radio inverts was measured, not assumed.</b> On the
    /// IC-7300 the mode name is no guide at all: CW normal is displayed "CW-U" and
    /// is physically the lower-sideband case, and RTTY normal is displayed
    /// "RTTY-L" and behaves the same way. That was established on 2026-09-24 by
    /// sweeping the tuner's own mark filter across the audio while moving the dial
    /// - up 500 Hz on the dial moved the received tone up with it, and audio that
    /// rises with the dial is the lower-sideband case. So RTTY normal passes
    /// <c>lowerSideband: true</c> and RTTY-R ("RTTY-U" in the UI) passes false.
    /// Those two modes are the only ones this is offered in; see
    /// <see cref="SidebandForFskMode"/> for why.</para>
    /// </summary>
    public static class RttyMarkCentre
    {
        /// <summary>
        /// The smallest cap this will ever use, and the one it uses when the
        /// caller cannot say how wide the receiver's passband is.
        ///
        /// A tone measurement is only ever evidence about the signal the analyser
        /// happened to pick, and the further that signal sits from where the
        /// operator was listening, the likelier it is a different station
        /// altogether. So there is a limit on how far one press may move the dial.
        ///
        /// <para><b>This was the whole cap until 2026-10-09, on the reasoning that
        /// half a kilohertz is "wider than any mistuning that still leaves a signal
        /// in the passband at all". The bench disproved that the same day.</b>
        /// DDK9 was copying at a 5% character error rate with its mark 619 Hz away
        /// from the 2125 Hz the tuner was set to, through a 1200 Hz filter - well
        /// inside the passband, decoding, and refused twice by this cap with
        /// "dial left alone" in the log. The operator then had to move the VFO by
        /// hand, which is the one thing this feature exists to save.</para>
        ///
        /// <para>The honest bound is not a constant at all: <b>it is the width of
        /// the filter the signal arrived through</b>. A tone further from the
        /// wanted mark than the passband is wide cannot have been audible, so it
        /// was some other signal; a tone nearer than that came through the same
        /// filter the operator is listening to and is fair game. Callers that know
        /// the IF width pass it as <c>maxOffsetHz</c>; this constant remains the
        /// floor, so the behaviour can never become more restrictive than it was
        /// when it was the only rule.</para>
        /// </summary>
        public const double MaxOffsetHz = 500.0;

        /// <summary>
        /// Below this the dial is left alone.
        ///
        /// <see cref="RttySignalAnalyser"/> measures tones from 4096-point spectra,
        /// which at 48 kHz is one bin every 11.7 Hz, and its peak interpolation
        /// does better than a bin but not by much: on the bench a true 450 Hz shift
        /// read back between 444 and 450, so a few hertz of error on each tone.
        /// Two bins is therefore the smallest correction that is more signal than
        /// noise, and a feature that moved the radio by less than that in response
        /// to measurement jitter would be a feature that fidgets. The same figure,
        /// for the same reason, is the CW reader's zero-in floor.
        /// </summary>
        public const double MinOffsetHz = 25.0;

        /// <summary>
        /// How confident the measurement has to be before the dial is moved.
        ///
        /// 0.4 is the figure the shared tuner itself gives up at - below it the
        /// browser tells the operator it could not make sense of the signal and
        /// applies nothing - so it is already the line between an answer and a
        /// shrug. Moving the radio on evidence the UI would not even display would
        /// be acting on less than it reports.
        /// </summary>
        public const double MinConfidence = 0.4;

        /// <summary>
        /// Hz to add to the current VFO frequency so that
        /// <paramref name="measuredMarkHz"/> arrives at
        /// <paramref name="wantedMarkHz"/>; null when the measurement should not be
        /// acted on.
        ///
        /// <para>Null means do nothing, and it is deliberately not zero: no move
        /// and a zero-hertz move look identical on the radio, but only one of them
        /// should be announced to the operator as having happened.</para>
        /// </summary>
        /// <param name="measuredMarkHz">The mark tone the analyser found, in audio Hz.</param>
        /// <param name="wantedMarkHz">Where the tuner's mark filter sits, in audio Hz.</param>
        /// <param name="lowerSideband">
        /// True when audio rises as the dial rises, which is RTTY normal on this
        /// radio. From <see cref="SidebandForFskMode"/>.
        /// </param>
        /// <param name="confidence">The analyser's confidence, 0-1.</param>
        /// <param name="maxOffsetHz">
        /// The receiver's IF passband width in Hz, when the caller knows it: a tone
        /// further away than the passband is wide was never audible and so was a
        /// different signal. Null, zero or anything below
        /// <see cref="MaxOffsetHz"/> uses <see cref="MaxOffsetHz"/> instead, which
        /// makes this parameter incapable of tightening the guard - only of letting
        /// a wide filter say honestly how wide it is.
        /// </param>
        public static long? ComputeOffsetHz(
            double  measuredMarkHz,
            double  wantedMarkHz,
            bool    lowerSideband,
            double  confidence   = 1.0,
            double? maxOffsetHz  = null)
        {
            // Max, not coalesce: a caller reporting a 250 Hz CW filter must not be
            // able to make this stricter than the constant it is replacing. The
            // narrow-filter case is already handled where it belongs, by the
            // advisory that tells the operator their filter is too narrow to have
            // seen the shift at all.
            var cap = maxOffsetHz is { } m && m > MaxOffsetHz ? m : MaxOffsetHz;

            var offset = CwZeroIn.ComputeOffsetWholeHz(
                measuredToneHz: measuredMarkHz,
                targetPitchHz:  wantedMarkHz,
                lowerSideband:  lowerSideband,
                maxOffsetHz:    cap,
                confidence:     confidence,
                minConfidence:  MinConfidence);

            // The floor is applied here rather than passed down, because CwZeroIn
            // has no notion of one: its caller wants the arithmetic, and whether a
            // small answer is worth acting on is a question about this feature.
            if (offset is null || Math.Abs(offset.Value) < MinOffsetHz) return null;
            return offset;
        }

        /// <summary>
        /// Whether <paramref name="mode"/> is one of the radio's own FSK modes and,
        /// if it is, which way it inverts. Null for everything else.
        ///
        /// <para><b>Only the FSK modes, on purpose.</b> In an AFSK mode - DATA-L,
        /// DATA-U, LSB, USB - the tones are made by the operator's own software and
        /// the radio is a plain SSB transceiver. The same arithmetic would apply,
        /// but two things would not: which way those modes invert has never been
        /// measured on this radio the way RTTY's was, and an operator running MMTTY
        /// or fldigi has their own tuning indicator and their own idea of where the
        /// dial belongs. Moving their VFO out from under them on the strength of an
        /// unverified sign would be the sort of help nobody asked for.</para>
        /// </summary>
        /// <returns>
        /// True for RTTY normal, false for RTTY-R, null when the mode is not FSK.
        /// </returns>
        public static bool? SidebandForFskMode(string? mode)
        {
            // These are display strings - the UI's vocabulary, not sideband claims.
            // "RTTY-L" is RTTY normal (CI-V mode 0x04) and "RTTY-U" is RTTY-R
            // (0x08); see the note on the class for why the names read backwards
            // from the physics on this radio.
            if (string.Equals(mode, "RTTY-U", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(mode, "RTTY-L", StringComparison.OrdinalIgnoreCase)
             || string.Equals(mode, "RTTY",   StringComparison.OrdinalIgnoreCase)) return true;
            return null;
        }
    }
}
