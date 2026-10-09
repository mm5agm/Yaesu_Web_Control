using System;

namespace RadioWebControl.Core.Services.Rtty
{
    /// <summary>
    /// How much IF passband an FSK signal actually needs, given where the radio
    /// puts that passband.
    ///
    /// <para>This exists to answer one narrow question - <i>is the filter the
    /// radio is already using too narrow to have carried this signal at all?</i> -
    /// and it is used in one direction only. A caller may widen the filter to the
    /// figure this returns. <b>Nothing may narrow a filter to it.</b> A narrow
    /// filter is how a RTTY operator digs a signal out of a crowded band, and the
    /// width they chose is evidence about the band this software does not have.</para>
    ///
    /// <para><b>The passband does not follow the tones, so the shift alone cannot
    /// answer this.</b> An earlier version of this class returned
    /// <c>shift + 2 * baud</c> - the textbook occupied bandwidth - and its own
    /// documentation admitted the figure depended on "where the radio centres its
    /// RTTY passband relative to the mark pitch, which has never been measured on
    /// the bench here". It has now been measured, on both radios, and the textbook
    /// figure is wrong on both:</para>
    ///
    /// <list type="bullet">
    ///   <item><description><b>IC-7300 MkII</b> centres the passband on the
    ///   <i>mark</i> tone. At 450 Hz shift and 50 baud a 550 Hz filter - exactly
    ///   what the old sum asked for - put the space tone 33 dB down and decoded
    ///   <i>zero</i> characters. 1200 Hz decoded cleanly.</description></item>
    ///   <item><description><b>FTdx101MP</b> holds the passband at a fixed audio
    ///   frequency near 1800 Hz, following neither tone. The same signal needed
    ///   1700 Hz with the pair at 2125/2575, and only 800 Hz with the identical
    ///   pair moved down to 1675/2125 - a factor of two from the audio placement
    ///   alone, with the shift and the speed unchanged.</description></item>
    /// </list>
    ///
    /// <para>So the floor is set by whichever tone sits <i>furthest from the
    /// centre of the passband</i>, not by the gap between the tones. A tone at
    /// audio frequency f survives a passband of width W centred on C while
    /// |f - C| &lt;= W/2, and each tone carries keying sidebands out to roughly
    /// the baud rate either side, which gives the sum below. Fourteen bench
    /// points across those two radios and two tone placements all agree with it;
    /// the old sum agreed with three of them.</para>
    ///
    /// <para><b>The centre is radio-specific and the caller supplies it</b> -
    /// that is the seam. Core does the geometry; which audio frequency a given
    /// radio centres its RTTY passband on is a fact about that radio and belongs
    /// in the app that talks to it. Both tone frequencies are passed in rather
    /// than a shift, because the tuner already knows both and deriving space from
    /// mark would mean re-deriving the sideband sign that
    /// <see cref="RttyMarkCentre"/> owns.</para>
    /// </summary>
    public static class RttyIfWidth
    {
        /// <summary>
        /// The narrowest passband, in Hz, that could have carried both tones -
        /// or null when the geometry given cannot support an answer.
        ///
        /// <para>Read it as a floor and nothing more: a filter narrower than this
        /// was certainly clipping one of the tones, while a filter wider than it
        /// may still be the wrong choice for the band. The radio's own filter
        /// widths are quoted at -6 dB and its skirts are not vertical, so this is
        /// the point below which the loss is certain, not the point above which
        /// there is none.</para>
        ///
        /// <para>Null means "cannot say", and a caller must not substitute a
        /// guess. Writing a width on no evidence is the failure this whole class
        /// is here to avoid.</para>
        /// </summary>
        /// <param name="markHz">Mark tone, in Hz of audio.</param>
        /// <param name="spaceHz">Space tone, in Hz of audio. May be above or
        /// below the mark; only the distance from the centre matters.</param>
        /// <param name="baud">Keying speed in transitions per second.</param>
        /// <param name="passbandCentreHz">
        /// The audio frequency this radio centres its IF passband on. For a
        /// mark-centred radio that is the mark tone itself; for one with a fixed
        /// passband it is that fixed frequency.
        /// </param>
        public static int? MinimumHz(double markHz, double spaceHz, double baud, double passbandCentreHz)
        {
            if (!Usable(markHz) || !Usable(spaceHz) || !Usable(passbandCentreHz)) return null;
            if (double.IsNaN(baud) || double.IsInfinity(baud) || baud < 0) return null;

            double furthest = Math.Max(
                Math.Abs(markHz  - passbandCentreHz),
                Math.Abs(spaceHz - passbandCentreHz));

            // Rounded up, because this is a floor: rounding a floor down would
            // quietly return a width that clips.
            return (int)Math.Ceiling(2.0 * furthest + 2.0 * baud);
        }

        /// <summary>
        /// Whether a passband of this width, centred where this radio centres it,
        /// actually passes both tones.
        ///
        /// <para>For telling the operator the truth when the answer is no and
        /// widening cannot fix it. If the tone pair sits far enough off the centre
        /// of the passband, the radio's <i>widest</i> filter may still clip a tone,
        /// and then the thing to change is the dial, not the filter - so a caller
        /// that only ever widened would report success and leave the operator
        /// watching an empty screen.</para>
        /// </summary>
        public static bool Passes(int widthHz, double markHz, double spaceHz, double baud, double passbandCentreHz)
            => widthHz > 0
               && MinimumHz(markHz, spaceHz, baud, passbandCentreHz) is { } needed
               && widthHz >= needed;

        /// <summary>
        /// The width to ask the radio for, given what it is already using: null
        /// when nothing should be written, otherwise the floor.
        ///
        /// <para>Returning null rather than the unchanged width is what keeps the
        /// widen-only rule honest at the call site. A caller handed a number will
        /// write it, and a write that happens to be a no-op still costs a CI-V
        /// exchange on a 19200-baud bus that the scope and the meters are already
        /// sharing - and still has to be undone on the way out, because the
        /// restore cannot tell a width this code set from one the operator
        /// chose.</para>
        /// </summary>
        /// <param name="currentWidthHz">
        /// The width the radio reports now. Zero or negative means the width could
        /// not be read, in which case nothing is written - a filter of unknown
        /// width is not evidence of a problem, and guessing at one would be
        /// writing on no evidence at all.
        /// </param>
        /// <param name="markHz">Mark tone, in Hz of audio.</param>
        /// <param name="spaceHz">Space tone, in Hz of audio.</param>
        /// <param name="baud">Keying speed in transitions per second.</param>
        /// <param name="passbandCentreHz">
        /// The audio frequency this radio centres its IF passband on.
        /// </param>
        public static int? WidenToHz(
            int currentWidthHz, double markHz, double spaceHz, double baud, double passbandCentreHz)
        {
            if (currentWidthHz <= 0) return null;
            if (MinimumHz(markHz, spaceHz, baud, passbandCentreHz) is not { } needed) return null;
            if (needed <= 0) return null;
            return currentWidthHz >= needed ? null : needed;
        }

        private static bool Usable(double hz) => !double.IsNaN(hz) && !double.IsInfinity(hz) && hz > 0;
    }
}
