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
    /// A straight port of <c>loSlideHz</c> in if-out-offset.js, measured on
    /// Colin's FTdx101MP on 2026-09-11 and again, with the sideband taken
    /// into account, on 2026-09-24 (discussion #172) — see
    /// docs/design/sdr-spectrum-axis-investigation.md. The radio holds the
    /// centre of its filter still in the IF OUT, so the slide is that
    /// centre's RF offset from the dial, and it changes sign in a
    /// lower-sideband mode. The two copies are held together by a test
    /// that parses the constants out of the JavaScript
    /// (YaesuIfOutOffsetTests). Change one, and the test says so.
    /// </para>
    /// </remarks>
    public static class YaesuIfOutOffset
    {
        /// <summary>The Yaesu default CW pitch, used when the radio has not said.</summary>
        private const int DefaultCwPitchHz = 700;

        /// <summary>
        /// Audio frequency the SSB passband is centred on, at every DSP width,
        /// and the DATA SHIFT (SSB) menu's default. In DATA-L/U the radio
        /// centres on whatever that menu says instead (measured at 1000 Hz on
        /// 2026-09-24), so DATA uses the value read from the radio and falls
        /// back to this. Must match SSB_CARRIER_POINT_HZ_101 in if-out-offset.js.
        /// </summary>
        public const int SsbCarrierPointHz101 = 1500;

        /// <summary>
        /// RF offset of the RTTY filter centre (mark + shift/2) from the dial,
        /// the same in RTTY-L and RTTY-U. Must match RTTY_CENTRE_HZ_101.
        /// </summary>
        public const int RttyCentreHz101 = -85;

        /// <summary>
        /// The CW slide stops growing at this width. Must match
        /// CW_MAX_SLIDE_WIDTH_HZ_101.
        /// </summary>
        public const int CwMaxSlideWidthHz101 = 3000;

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
        /// <param name="dataShiftHz">The DATA SHIFT (SSB) menu (EX010405) in Hz; DATA-L/U only.</param>
        public static int LoSlideHz(string? model, string? mode, int? ifWidthHz, int ifShiftHz, int cwPitchHz,
                                    int dataShiftHz = SsbCarrierPointHz101)
        {
            if (model is null || RadioCapabilities.SdrIfOutHz(model, "A") is null) return 0;

            string m     = (mode ?? "").ToUpperInvariant();
            int    pitch = cwPitchHz > 0 ? cwPitchHz : DefaultCwPitchHz;
            int    side  = m is "LSB" or "DATA-L" or "CW-L" or "RTTY-L" ? -1 : 1;

            if (m is "CW-U" or "CW-L")
            {
                // The radio keeps the CW filter's lower edge at or above half
                // the pitch by moving the LO instead of the passband.
                int w = Math.Min(ifWidthHz ?? 0, CwMaxSlideWidthHz101);
                return side * (ifShiftHz + Math.Max(0, (w - pitch) / 2));
            }
            if (m is "DATA-L" or "DATA-U")
                return side * (dataShiftHz + ifShiftHz);
            if (m is "LSB" or "USB")
                return side * (SsbCarrierPointHz101 + ifShiftHz);
            if (m is "RTTY-L" or "RTTY-U")
                return RttyCentreHz101 + side * ifShiftHz;
            if (m.StartsWith("AM", StringComparison.Ordinal))
                return 0;   // centred on the carrier; IF SHIFT does not move it
            // FM / DATA-FM / PSK: shift only — unmeasured there.
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
        public static long? DialIfHz(string model, string vfo, string? mode, string? ifWidthCode, int ifShiftHz, int cwPitchCode,
                                     int dataShiftHz = SsbCarrierPointHz101)
        {
            if (RadioCapabilities.SdrIfOutHz(model, vfo) is not long ifOut) return null;
            int? widthHz = YaesuIfWidth.HzForCode(model, mode, ifWidthCode);
            int  pitchHz = 300 + Math.Clamp(cwPitchCode, 0, 75) * 10;
            return ifOut + LoSlideHz(model, mode, widthHz, ifShiftHz, pitchHz, dataShiftHz);
        }
    }
}
