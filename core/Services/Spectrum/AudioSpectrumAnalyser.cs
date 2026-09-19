namespace RadioWebControl.Core.Services.Spectrum
{
    /// <summary>
    /// Turns a stream of mono audio into the small byte-per-bin spectrum a
    /// filter-shape panel draws inside its passband: what the receiver is
    /// actually passing, analysed the same way the radio's own filter display
    /// does it -- from the audio after the filter, not from RF.
    ///
    /// Output is deliberately shaped like the browser's AnalyserNode
    /// getByteFrequencyData(): one byte per bin, 0 at <see cref="MinDb"/> and
    /// below, 255 at <see cref="MaxDb"/> and above, with the same first-order
    /// smoothing between frames. A panel that already consumes the browser
    /// analyser (Remote Audio's RX path) consumes this unchanged, and a host
    /// that has the radio's audio but no browser session can feed it too.
    ///
    /// Bins are kept only up to <see cref="MaxHz"/>: an audio passband never
    /// reaches past a few kHz, and 256 bytes a frame is what makes pushing
    /// this over a websocket ten times a second cost nothing.
    ///
    /// Pure arithmetic on samples. Nothing here knows which radio the audio
    /// came from, so it lives in core.
    /// </summary>
    public sealed class AudioSpectrumAnalyser
    {
        public int    SampleRate { get; }
        public int    FftSize    { get; }
        public double MaxHz      { get; }
        public double MinDb      { get; }
        public double MaxDb      { get; }
        public double Smoothing  { get; }

        /// <summary>Hz per bin: <c>SampleRate / FftSize</c>.</summary>
        public double BinHz => (double)SampleRate / FftSize;

        /// <summary>Number of bins kept, covering 0 Hz up to <see cref="MaxHz"/>.</summary>
        public int BinCount { get; }

        private readonly double[] _window;
        private readonly double[] _re;
        private readonly double[] _im;
        private readonly double[] _smoothedDb;
        private readonly float[]  _fill;
        private readonly byte[]   _bins;
        private int _filled;
        private readonly double _scale;

        /// <param name="sampleRate">Audio rate in Hz.</param>
        /// <param name="fftSize">Power of two. 2048 at 48 kHz is 23.4 Hz per bin and a frame every 43 ms.</param>
        /// <param name="maxHz">Highest frequency to keep a bin for.</param>
        /// <param name="minDb">Level (dBFS, full-scale sine = 0) that maps to byte 0.</param>
        /// <param name="maxDb">Level that maps to byte 255.</param>
        /// <param name="smoothing">0 = every frame as measured; 0.8 = the browser analyser's default.</param>
        public AudioSpectrumAnalyser(
            int sampleRate,
            int fftSize = 2048,
            double maxHz = 6000,
            double minDb = -100,
            double maxDb = -30,
            double smoothing = 0.7)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (fftSize < 16 || (fftSize & (fftSize - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(fftSize), "FFT size must be a power of two.");
            if (maxDb <= minDb) throw new ArgumentException("maxDb must exceed minDb.");
            if (smoothing < 0 || smoothing >= 1) throw new ArgumentOutOfRangeException(nameof(smoothing));

            SampleRate = sampleRate;
            FftSize    = fftSize;
            MaxHz      = maxHz;
            MinDb      = minDb;
            MaxDb      = maxDb;
            Smoothing  = smoothing;

            BinCount = Math.Clamp((int)Math.Ceiling(maxHz / BinHz), 1, fftSize / 2);

            _window     = new double[fftSize];
            _re         = new double[fftSize];
            _im         = new double[fftSize];
            _smoothedDb = new double[BinCount];
            _fill       = new float[fftSize];
            _bins       = new byte[BinCount];
            Array.Fill(_smoothedDb, minDb);

            // Hann window, and a scale that puts a full-scale sine at 0 dBFS
            // in its bin: the coherent gain of Hann is N/2, and a real sine
            // splits its energy between +f and -f, so the peak bin is N/4.
            double windowSum = 0;
            for (int i = 0; i < fftSize; i++)
            {
                _window[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (fftSize - 1));
                windowSum += _window[i];
            }
            _scale = 2.0 / windowSum;
        }

        /// <summary>
        /// The latest frame, one byte per bin from 0 Hz upward. Valid after
        /// <see cref="Push"/> has returned true at least once; all zero before.
        /// The buffer is reused -- copy it if it must outlive the next push.
        /// </summary>
        public ReadOnlySpan<byte> Bins => _bins;

        /// <summary>
        /// Feed samples. Returns true each time a full FFT frame has been
        /// completed and <see cref="Bins"/> updated -- possibly more than once
        /// per call for a large input, in which case only the last frame is
        /// left in <see cref="Bins"/>. Frames do not overlap.
        /// </summary>
        public bool Push(ReadOnlySpan<float> samples)
        {
            bool produced = false;
            int offset = 0;

            while (offset < samples.Length)
            {
                int take = Math.Min(FftSize - _filled, samples.Length - offset);
                samples.Slice(offset, take).CopyTo(_fill.AsSpan(_filled));
                _filled += take;
                offset  += take;

                if (_filled == FftSize)
                {
                    Analyse();
                    _filled  = 0;
                    produced = true;
                }
            }

            return produced;
        }

        /// <summary>Forget any partial frame and the smoothing history.</summary>
        public void Reset()
        {
            _filled = 0;
            Array.Fill(_smoothedDb, MinDb);
            Array.Clear(_bins);
        }

        private void Analyse()
        {
            for (int i = 0; i < FftSize; i++)
            {
                _re[i] = _fill[i] * _window[i];
                _im[i] = 0;
            }

            Fft.Transform(_re, _im);

            double range = MaxDb - MinDb;
            for (int k = 0; k < BinCount; k++)
            {
                double mag = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) * _scale;
                double db  = 20.0 * Math.Log10(Math.Max(mag, 1e-12));

                // Same recurrence as the Web Audio analyser: the displayed
                // value leans on the previous one, so noise stops flickering
                // without a real signal lagging much.
                db = Smoothing * _smoothedDb[k] + (1.0 - Smoothing) * db;
                _smoothedDb[k] = db;

                double t = (db - MinDb) / range;
                _bins[k] = (byte)Math.Clamp(Math.Round(t * 255.0), 0, 255);
            }
        }
    }
}
