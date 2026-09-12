// Yaesu Web Control — SpectrumSpanPlan
//
// Turns the span an operator picked into what the SDR worker is actually
// asked to do. The span list is the FTdx101's own scope list — 1k, 2k, 5k,
// 10k, 20k, 50k, 100k, 200k, 500k, 1M — plus 2M, which the SDR can do and
// the radio cannot. The SDR cannot be run at most of those rates, so each
// span is served one of two ways:
//
//   * 100 kHz and below: the hardware sits at a fixed 125 kHz and the worker
//     runs a 16k-point FFT (7.6 Hz per bin), crops the bins under the wanted
//     window and sends only those. The span is then a display property —
//     switching between 1 k and 100 k is one control message to a running
//     worker, with no retune, no blank trace and no chance to leak the
//     device on the way (see project memory on SDRplayAPIService leaks).
//
//   * Above that: the hardware runs at the tightest rate SdrplayDevice.PlanFor
//     can reach that still covers the span (250 k, 500 k, 1 M, 2 M), and
//     the worker crops to the span where the two differ — 200 kHz is a crop
//     of the 250 kHz stream; 500 kHz and up are the stream itself. A change
//     of hardware rate is a worker respawn.
//
// The dividing line is the RSP1's decimation floor. Below 125 kHz the API
// would have to decimate by 32 to get bins as fine as a CW operator wants,
// and the frame rate would fall with the bin width — an N-point FFT gives
// R/N Hz per bin and R/N frames a second, the same number. Overlapping the
// FFTs in software (hop < N) breaks that link; decimating harder does not.
//
// Radio-specific in one respect only: a crop is centred on the radio's IF
// OUT frequency (the dial), not on where the SDR happens to be tuned, which
// on the FTdx101 is 5 kHz below it. Pure DSP lives in core (SpectrumZoom);
// this file is the YWC-side policy for driving it.

namespace Yaesu_Web_Control.Services.Sdr
{
    public static class SpectrumSpanPlan
    {
        /// <summary>Hardware rate held for every software-zoom span.</summary>
        public const double ZoomRateHz = 125_000;

        /// <summary>FFT length in zoom mode: 125 kHz / 16384 = 7.63 Hz per bin.</summary>
        public const int ZoomFftSize = 16384;

        /// <summary>
        /// Samples between successive FFTs in zoom mode. A quarter window
        /// gives ~30 FFTs a second at 125 kHz, above the worker's 25/s send
        /// cap, at a cost of ~30 sixteen-k FFTs a second per worker.
        /// </summary>
        public const int ZoomHopSize = 4096;

        /// <summary>
        /// Every span the UI offers, in Hz — the FTdx101's own scope spans
        /// and 2 MHz. Must agree with the span buttons in Index.cshtml and
        /// the Settings page select.
        /// </summary>
        public static readonly double[] ValidSpans =
        [
            1_000, 2_000, 5_000, 10_000, 20_000, 50_000, 100_000,
            200_000, 500_000, 1_000_000, 2_000_000,
        ];

        /// <summary>
        /// Hardware rates the wide regime may ask for, ascending. Each is a
        /// row of SdrplayDevice.PlanFor; a span is served by the first one
        /// that covers it.
        /// </summary>
        private static readonly double[] WideRatesHz = [250_000, 500_000, 1_000_000, 2_000_000];

        public static bool IsValid(double spanHz) => Array.IndexOf(ValidSpans, spanHz) >= 0;

        /// <summary>True when the span is served by cropping the fixed 125 kHz stream.</summary>
        public static bool IsZoom(double spanHz) => spanHz <= ZoomRateHz;

        /// <summary>What to start (or retask) a worker with for one span.</summary>
        /// <param name="HardwareRateHz">Sample rate the SDR is configured for.</param>
        /// <param name="FftSize">FFT length the worker runs.</param>
        /// <param name="HopSize">Samples between FFTs; equals FftSize when not overlapped.</param>
        /// <param name="ViewCentreHz">Where the dial sits in the stream — the centre of any crop.</param>
        /// <param name="ViewSpanHz">Width of the cropped window, or 0 for "send the whole stream".</param>
        public readonly record struct Plan(
            double HardwareRateHz,
            int    FftSize,
            int    HopSize,
            long   ViewCentreHz,
            long   ViewSpanHz)
        {
            /// <summary>True when the worker crops its FFT rather than sending all of it.</summary>
            public bool Crops => ViewSpanHz > 0;

            /// <summary>
            /// True when a worker running <paramref name="other"/> can be
            /// retasked to this plan by a ViewWindow message alone — the
            /// device and FFT settings are the same, only the crop differs.
            /// </summary>
            public bool SameHardwareAs(Plan other) =>
                HardwareRateHz == other.HardwareRateHz &&
                FftSize        == other.FftSize        &&
                HopSize        == other.HopSize;
        }

        /// <summary>
        /// Plan for <paramref name="spanHz"/>.
        /// </summary>
        /// <param name="spanHz">A member of <see cref="ValidSpans"/>.</param>
        /// <param name="wideFftSize">FFT size for the hardware-rate regime (the SdrFftSize setting).</param>
        /// <param name="sdrCentreHz">Where the SDR is tuned (SdrIfFrequencyHzA/B).</param>
        /// <param name="ifOutHz">
        /// The radio's IF OUT frequency for this receiver, i.e. where the dial
        /// sits in the SDR's stream, or null when it is not known for the
        /// model — a crop is then centred on the SDR's own tune frequency.
        /// </param>
        public static Plan For(double spanHz, int wideFftSize, long sdrCentreHz, long? ifOutHz)
        {
            // SpectrumZoom.Crop slides a window that would run off the edge
            // of the stream back inside it, so a centre a few kHz off the
            // SDR's tune point needs no clamping here.
            long centre = ifOutHz ?? sdrCentreHz;

            if (IsZoom(spanHz))
                return new Plan(ZoomRateHz, ZoomFftSize, ZoomHopSize, centre, (long)spanHz);

            double rate = WideRatesHz[^1];
            foreach (double r in WideRatesHz)
                if (r >= spanHz) { rate = r; break; }

            // A span that is exactly a hardware rate sends the whole stream
            // (span 0 = no crop); anything narrower is cropped out of the
            // next rate up.
            return new Plan(rate, wideFftSize, wideFftSize, centre, rate == spanHz ? 0 : (long)spanHz);
        }
    }
}
