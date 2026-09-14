// Yaesu Web Control – SoapySDR Device
// Implements ISdrDevice using the existing SoapySdrInterop P/Invoke wrapper.
// The SoapySDR API is pull-based (blocking ReadStream), so no Channel or
// callback bridging is needed.  Task.Run is used to keep the read off the
// async thread pool so it does not block other work.

namespace Yaesu_Web_Control.Services.Sdr
{
    public sealed class SoapySdrDevice : ISdrDevice
    {
        public string Key   { get; }
        public string Label { get; }

        private IntPtr _device;
        private IntPtr _stream;
        private int    _fftSize;
        private bool   _streaming;
        private bool   _disposed;

        /// <param name="key">
        /// The SoapySDR kwargs string returned by <see cref="SoapySdrInterop.EnumerateDevices"/>,
        /// e.g. <c>"driver=rtlsdr,label=Generic RTL2832U,serial=00000001"</c>.
        /// </param>
        public SoapySdrDevice(string key)
        {
            Key   = key;
            Label = ExtractLabel(key);
        }

        // ── ISdrDevice ────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public void Configure(long centreFrequencyHz, double sampleRateHz, int fftSize)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(SoapySdrDevice));

            _fftSize = fftSize;
            _device  = SoapySdrInterop.OpenDevice(Key);
            SoapySdrInterop.ConfigureRxChannel(_device, centreFrequencyHz, sampleRateHz);

            // SoapySDR is asked for this rate exactly and only setSampleRate is
            // bound, so there is nothing to read back — unlike the SDRplay path,
            // which reaches a narrow span by decimating a fixed hardware rate and
            // therefore lands on a value that can differ from the request. If a
            // Soapy backend is ever found to silently substitute a nearby rate,
            // bind SoapySDRDevice_getSampleRate and report that instead.
            ActualSampleRateHz = sampleRateHz;
        }

        /// <inheritdoc/>
        public double ActualSampleRateHz { get; private set; }

        /// <inheritdoc/>
        public void StartStreaming()
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(SoapySdrDevice));
            if (_streaming) return;

            _stream    = SoapySdrInterop.OpenRxStream(_device);
            _streaming = true;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// SoapySDR's ReadStream may return fewer samples than requested if the
        /// internal buffer is momentarily low.  This method retries within the
        /// timeout window so callers always receive a complete FFT frame or nothing.
        /// </remarks>
        public async ValueTask<bool> TryReadIqFrameAsync(
            float[] buffer, int timeoutMs, CancellationToken ct = default)
        {
            if (!_streaming) return false;

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (!ct.IsCancellationRequested)
            {
                int msLeft = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (msLeft <= 0) return false;

                // Cap the per-call native timeout at 50 ms so we can check ct regularly.
                int timeoutUs = Math.Clamp(msLeft * 1_000, 1, 50_000);

                int read = await Task.Run(
                    () => SoapySdrInterop.ReadIqSamples(
                              _device, _stream, buffer, _fftSize, timeoutUs),
                    ct).ConfigureAwait(false);

                if (read < 0)
                    throw new InvalidOperationException(
                        $"SoapySDR: ReadStream returned error code {read}");

                if (read >= _fftSize)
                    return true;

                // Partial read — the buffer may now contain stale data in the
                // tail; simply retry; the next call overwrites from the start.
            }

            return false;
        }

        /// <inheritdoc/>
        public void Stop()
        {
            if (!_streaming) return;
            _streaming = false;

            SoapySdrInterop.CloseRxStream(_device, _stream);
            _stream = IntPtr.Zero;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
            SoapySdrInterop.CloseDevice(_device);
            _device = IntPtr.Zero;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Extracts the human-readable "label" value from a SoapySDR kwargs string.
        /// Falls back to the full kwargs string if no label key is present.
        /// </summary>
        private static string ExtractLabel(string kwargs)
        {
            foreach (var pair in kwargs.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 &&
                    pair[..eq].Trim().Equals("label", StringComparison.OrdinalIgnoreCase))
                {
                    return pair[(eq + 1)..].Trim();
                }
            }
            return kwargs;
        }
    }
}
