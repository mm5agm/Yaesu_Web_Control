namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Where the dial frequency actually sits in the radio's IF OUT, in Hz —
    /// the C# side of <c>wwwroot/js/sdr/if-out-offset.js</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The radio does not hold its IF OUT still. On top of the fixed 9.005 /
    /// 8.900 MHz (<see cref="RadioCapabilities.SdrIfOutHz"/>) the FTdx101
    /// slides its LO with the DSP filter width, the IF shift and the CW
    /// pitch, so a signal on the dial moves through the SDR's stream as the
    /// operator works the filter. The browser has always corrected its axis
    /// for this; the software-zoom crop in the worker needs the same number
    /// so that a 1 kHz window is cut around the dial and not around a point
    /// up to 1.4 kHz away from it — which is where a 3.5 kHz CW filter puts
    /// it, and is how the dial marker went missing at 1k and 2k on
    /// 2026-09-12.
    /// </para>
    /// <para>
    /// The formula and the SSB table are a straight port of
    /// <c>loSlideHz</c> in if-out-offset.js, measured on Colin's FTdx101MP
    /// on 2026-09-11 — see docs/design/sdr-spectrum-axis-investigation.md.
    /// The two copies are held together by a test that parses the table
    /// out of the JavaScript (YaesuIfOutOffsetTests), the same way the IF
    /// width tables are. Change one, and the test says so.
    /// </para>
    /// </remarks>
    public static class YaesuIfOutOffset
    {
        /// <summary>The Yaesu default CW pitch, used when the radio has not said.</summary>
        private const int DefaultCwPitchHz = 700;

        /// <summary>
        /// SSB LO slide by DSP width, in Hz. Not a formula — measured point by
        /// point (SH codes 12..23; the codes below 12 are all zero) and rounded
        /// to the nearest 50 Hz. Keyed by width so a code-0 "default" width
        /// resolves the same way as the explicit code for the same bandwidth.
        /// Must match SSB_SLIDE_HZ_101 in if-out-offset.js.
        /// </summary>
        public static readonly IReadOnlyList<(int WidthHz, int SlideHz)> SsbSlideHz101 =
        [
            (2200,   50), (2300,  250), (2400,  350), (2500,  500), (2600,  650),
            (2700,  850), (2800, 1150), (2900, 1250), (3000, 1400), (3200, 1650),
        ];

        /// <summary>
        /// How far the radio has slid its LO for the current filter settings,
        /// relative to the narrow-filter, zero-shift case. Zero for a model
        /// with no measured IF OUT.
        /// </summary>
        /// <param name="model">RadioModel string, e.g. "FTdx101MP".</param>
        /// <param name="mode">The radio's mode string, e.g. "CW-U".</param>
        /// <param name="ifWidthHz">DSP width in Hz, or null when unknown.</param>
        /// <param name="ifShiftHz">IF shift in Hz, signed.</param>
        /// <param name="cwPitchHz">CW pitch in Hz; anything not positive falls back to 700.</param>
        public static int LoSlideHz(string? model, string? mode, int? ifWidthHz, int ifShiftHz, int cwPitchHz)
        {
            if (model is null || RadioCapabilities.SdrIfOutHz(model, "A") is null) return 0;

            string m     = (mode ?? "").ToUpperInvariant();
            int    pitch = cwPitchHz > 0 ? cwPitchHz : DefaultCwPitchHz;

            if (m is "CW-U" or "CW-L")
            {
                // The radio keeps the CW filter's lower edge at or above half
                // the pitch by moving the LO instead of the passband.
                int w = ifWidthHz ?? 0;
                return ifShiftHz + Math.Max(0, (w - pitch) / 2);
            }
            if (m is "LSB" or "USB" || m.StartsWith("DATA", StringComparison.Ordinal) || m.StartsWith("AM", StringComparison.Ordinal))
            {
                int slide = 0;
                if (ifWidthHz is int width)
                    foreach (var (w, hz) in SsbSlideHz101)
                        if (width >= w) slide = hz;
                return ifShiftHz + slide;
            }
            // RTTY / PSK / FM: shift only — the width term is unmeasured there.
            return ifShiftHz;
        }

        /// <summary>
        /// The IF frequency the dial sits at in this receiver's IF OUT, for
        /// the radio's current filter state: <see cref="RadioCapabilities.SdrIfOutHz"/>
        /// plus <see cref="LoSlideHz"/>. Null when the model's IF OUT has not
        /// been measured — the caller then centres on the SDR's own tune
        /// frequency, as before. Takes the raw CAT values the state service
        /// holds (SH width code, KP pitch code) so every caller converts them
        /// the same way.
        /// </summary>
        public static long? DialIfHz(string model, string vfo, string? mode, string? ifWidthCode, int ifShiftHz, int cwPitchCode)
        {
            if (RadioCapabilities.SdrIfOutHz(model, vfo) is not long ifOut) return null;
            int? widthHz = YaesuIfWidth.HzForCode(model, mode, ifWidthCode);
            int  pitchHz = 300 + Math.Clamp(cwPitchCode, 0, 75) * 10;
            return ifOut + LoSlideHz(model, mode, widthHz, ifShiftHz, pitchHz);
        }
    }
}
