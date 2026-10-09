using System;

namespace RadioWebControl.Core.Services.Rtty
{
    /// <summary>
    /// How much IF passband an FSK signal actually needs.
    ///
    /// <para>This exists to answer one narrow question - <i>is the filter the
    /// radio is already using too narrow to have carried this signal at all?</i> -
    /// and it is used in one direction only. A caller may widen the filter to the
    /// figure this returns. <b>Nothing may narrow a filter to it.</b></para>
    ///
    /// <para>The asymmetry is not timidity, it is the state of the evidence. A
    /// filter wider than the signal cannot remove anything the signal needed; a
    /// filter narrowed to a computed figure can, and whether it does depends on
    /// where the radio centres its RTTY passband relative to the mark pitch -
    /// which has never been measured on the bench here. Get that wrong and the
    /// space tone is attenuated at the edge of the passband, which is exactly the
    /// unequal-amplitude failure that <see cref="RttyMarkCentre"/> was written to
    /// cure. There is also an operator in this: a narrow filter is how a RTTY
    /// operator digs a signal out of a crowded band, and the width they chose is
    /// evidence about the band this software does not have.</para>
    ///
    /// <para>The sum itself is the ordinary one. Keying a carrier between two tones
    /// at <paramref name="baud"/> transitions a second puts sidebands either side
    /// of each tone at roughly the keying rate, so the occupied bandwidth is the
    /// shift plus about twice the baud rate. For the classic 170 Hz / 45.45 baud
    /// amateur signal that comes to 261 Hz, which is why every RTTY rig ever built
    /// offers a filter near 250-300 Hz: the arithmetic agreeing with eighty years
    /// of practice is the only confirmation available for a formula like this.</para>
    /// </summary>
    public static class RttyIfWidth
    {
        /// <summary>
        /// The narrowest passband, in Hz, that could have carried a signal of this
        /// shift and speed.
        ///
        /// <para>Read it as a floor and nothing more: a filter narrower than this
        /// was certainly clipping the signal, while a filter wider than it may
        /// still be the wrong choice for the band. The radio's own filter widths
        /// are quoted at -6 dB and its skirts are not vertical, so this is the
        /// point below which the loss is certain, not the point above which there
        /// is none.</para>
        /// </summary>
        /// <param name="shiftHz">Mark-to-space separation in Hz.</param>
        /// <param name="baud">Keying speed in transitions per second.</param>
        public static int MinimumHz(double shiftHz, double baud)
        {
            if (double.IsNaN(shiftHz) || shiftHz <= 0) shiftHz = 0;
            if (double.IsNaN(baud)    || baud    <= 0) baud    = 0;

            // Rounded up, because this is a floor: rounding a floor down would
            // quietly return a width that clips.
            return (int)Math.Ceiling(shiftHz + 2.0 * baud);
        }

        /// <summary>
        /// The width to ask the radio for, given what it is already using: the
        /// current width when that is already enough, otherwise the floor. Null
        /// when nothing should be written.
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
        /// <param name="shiftHz">Mark-to-space separation in Hz.</param>
        /// <param name="baud">Keying speed in transitions per second.</param>
        public static int? WidenToHz(int currentWidthHz, double shiftHz, double baud)
        {
            if (currentWidthHz <= 0) return null;
            var needed = MinimumHz(shiftHz, baud);
            if (needed <= 0) return null;
            return currentWidthHz >= needed ? null : needed;
        }
    }
}
