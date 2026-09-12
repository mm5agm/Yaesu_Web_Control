// Yaesu Web Control — SpectrumSpanPlan
//
// Turns the span an operator picked into what the SDR worker is actually
// asked to do. Two regimes:
//
//   * 250 kHz and up: span IS the hardware sample rate, as it always was.
//     SdrplayDevice.PlanFor maps each rate onto a decimation the API's
//     low-IF rules allow; a change of span is a worker respawn.
//
//   * 125 kHz and below: the hardware sits at a fixed 125 kHz and the worker
//     runs a 16k-point FFT (7.6 Hz per bin), crops the bins under the wanted
//     window and sends only those. The span is then a display property —
//     switching between 2.5 k and 125 k is one control message to a running
//     worker, with no retune, no blank trace and no chance to leak the
//     device on the way (see project memory on SDRplayAPIService leaks).
//
// The dividing line is the RSP1's decimation floor. Below 125 kHz the API
// would have to decimate by 32 to get bins as fine as a CW operator wants,
// and the frame rate would fall with the bin width — an N-point FFT gives
// R/N Hz per bin and R/N frames a second, the same number. Overlapping the
// FFTs in software (hop < N) breaks that link; decimating harder does not.
//
// Radio-specific in one respect only: the crop is centred on the radio's IF
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
        /// Every span the UI offers, in Hz. Must agree with the span buttons
        /// in Index.cshtml and the Settings page select. The wide half must
        /// also agree with SdrplayDevice.PlanFor.
        /// </summary>
        public static readonly double[] ValidSpans =
        [
            2_500, 5_000, 10_000, 25_000, 50_000, 125_000,
            250_000, 500_000, 1_000_000, 2_000_000,
        ];

        public static bool IsValid(double spanHz) => Array.IndexOf(ValidSpans, spanHz) >= 0;

        /// <summary>True when the span is served by cropping a fixed-rate stream.</summary>
        public static bool IsZoom(double spanHz) => spanHz <= ZoomRateHz;

        /// <summary>What to start (or retask) a worker with for one span.</summary>
        /// <param name="HardwareRateHz">Sample rate the SDR is configured for.</param>
        /// <param name="FftSize">FFT length the worker runs.</param>
        /// <param name="HopSize">Samples between FFTs; equals FftSize when not overlapped.</param>
        /// <param name="ViewCentreHz">Centre of the cropped window, or 0 for "send everything".</param>
        /// <param name="ViewSpanHz">Width of the cropped window, or 0 for "send everything".</param>
        public readonly record struct Plan(
            double HardwareRateHz,
            int    FftSize,
            int    HopSize,
            long   ViewCentreHz,
            long   ViewSpanHz)
        {
            public bool IsZoom => ViewSpanHz > 0;
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
        /// model — the crop is then centred on the SDR's own tune frequency.
        /// </param>
        public static Plan For(double spanHz, int wideFftSize, long sdrCentreHz, long? ifOutHz)
        {
            if (!IsZoom(spanHz))
                return new Plan(spanHz, wideFftSize, wideFftSize, 0, 0);

            // SpectrumZoom.Crop slides a window that would run off the edge
            // of the stream back inside it, so a centre a few kHz off the
            // SDR's tune point needs no clamping here; and asking for the
            // full 125 kHz simply yields the whole stream, thinned.
            long centre = ifOutHz ?? sdrCentreHz;
            return new Plan(ZoomRateHz, ZoomFftSize, ZoomHopSize, centre, (long)spanHz);
        }
    }
}
