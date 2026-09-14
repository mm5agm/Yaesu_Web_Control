// Yaesu Web Control – SDR Device Abstraction
// Hardware-agnostic interface so SdrBackgroundService is not coupled to any specific SDK.
// Implementations are responsible for bridging any callback-based native APIs
// to the pull-based TryReadIqFrameAsync consumer model.

namespace Yaesu_Web_Control.Services.Sdr
{
    /// <summary>
    /// Represents a single SDR receiver capable of delivering IQ samples.
    /// </summary>
    public interface ISdrDevice : IDisposable
    {
        /// <summary>Unique device key (e.g. "sdrplay:&lt;serialNumber&gt;").</summary>
        string Key { get; }

        /// <summary>Human-readable label for display in the UI.</summary>
        string Label { get; }

        /// <summary>
        /// Configure the device hardware.  Must be called before <see cref="StartStreaming"/>.
        /// </summary>
        /// <param name="centreFrequencyHz">Centre (RF) frequency in Hz.</param>
        /// <param name="sampleRateHz">IQ sample rate in Hz.</param>
        /// <param name="fftSize">Number of IQ samples per frame delivered by <see cref="TryReadIqFrameAsync"/>.</param>
        void Configure(long centreFrequencyHz, double sampleRateHz, int fftSize);

        /// <summary>
        /// The span the hardware actually produces, in Hz, valid after
        /// <see cref="Configure"/>. This is not always the <c>sampleRateHz</c> that
        /// was requested: a device may only support certain rates, and reaching a
        /// narrow span by decimating a fixed hardware rate lands on
        /// <c>hardwareRate / integerFactor</c> rather than on the exact request.
        /// Report <b>this</b> to the client — it is what the frequency scale must
        /// be drawn from, and echoing the request back instead skews the scale by
        /// the rounding error.
        /// </summary>
        double ActualSampleRateHz { get; }

        /// <summary>Begin hardware streaming.  <see cref="Configure"/> must have been called first.</summary>
        void StartStreaming();

        /// <summary>
        /// Waits asynchronously until a full FFT frame of IQ samples is available,
        /// then copies the data into <paramref name="buffer"/>.
        /// </summary>
        /// <param name="buffer">
        /// Receives interleaved I/Q float pairs.
        /// Must be at least <c>fftSize × 2</c> elements (as passed to <see cref="Configure"/>).
        /// </param>
        /// <param name="timeoutMs">Maximum wait in milliseconds before returning <c>false</c>.</param>
        /// <param name="ct">Optional cancellation token.</param>
        /// <returns><c>true</c> if a frame was written; <c>false</c> on timeout or cancellation.</returns>
        ValueTask<bool> TryReadIqFrameAsync(float[] buffer, int timeoutMs, CancellationToken ct = default);

        /// <summary>Stop streaming and release hardware resources.  Safe to call multiple times.</summary>
        void Stop();
    }
}
