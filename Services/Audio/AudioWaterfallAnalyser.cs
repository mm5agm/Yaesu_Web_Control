using System;
using RadioWebControl.Core.Services.Spectrum;

namespace Yaesu_Web_Control.Services.Audio
{
    /// <summary>
    /// The audio waterfall's analysis: the radio's RX audio (the same USB audio
    /// WSJT-X decodes) cut into overlapping Hann-windowed blocks and turned into
    /// a level in dBFS for each bin from 0 Hz to <see cref="MaxHz"/>.
    ///
    /// 8192 points at 48 kHz is 5.86 Hz a bin: fine enough to see FT8's eight
    /// tones 6.25 Hz apart as separate lines, and Piccolo's 20 Hz comb. A new
    /// block starts every <see cref="HopSize"/> samples (43 ms), so a signal
    /// shows up within a few rows of starting even though each block is 171 ms
    /// long.
    ///
    /// No smoothing between frames: the waterfall wants every row sharp, and
    /// the mode guesser (<see cref="AudioModeClassifier"/>) wants the levels as
    /// measured. Pure arithmetic; nothing here knows which radio is on the end.
    /// </summary>
    public sealed class AudioWaterfallAnalyser
    {
        public const int DefaultFftSize = 8192;
        public const int DefaultHopSize = 2048;
        public const double DefaultMaxHz = 4000;

        public int SampleRate { get; }
        public int FftSize { get; }
        public int HopSize { get; }
        public double MaxHz { get; }

        /// <summary>Hz per bin: SampleRate / FftSize.</summary>
        public double BinHz => (double)SampleRate / FftSize;

        /// <summary>Seconds between frames: HopSize / SampleRate.</summary>
        public double FrameSeconds => (double)HopSize / SampleRate;

        /// <summary>Bins kept, from 0 Hz up to MaxHz.</summary>
        public int BinCount { get; }

        private readonly double[] _window;
        private readonly double[] _re;
        private readonly double[] _im;
        private readonly float[] _ring;
        private readonly float[] _db;
        private readonly double _scale;
        private int _filled;
        private int _write;
        private int _sinceLast;

        public AudioWaterfallAnalyser(
            int sampleRate = AudioConstants.SampleRate,
            int fftSize = DefaultFftSize,
            int hopSize = DefaultHopSize,
            double maxHz = DefaultMaxHz)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (fftSize < 16 || (fftSize & (fftSize - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(fftSize), "FFT size must be a power of two.");
            if (hopSize <= 0 || hopSize > fftSize) throw new ArgumentOutOfRangeException(nameof(hopSize));

            SampleRate = sampleRate;
            FftSize = fftSize;
            HopSize = hopSize;
            MaxHz = maxHz;
            BinCount = Math.Clamp((int)Math.Ceiling(maxHz / BinHz), 1, fftSize / 2);

            _window = new double[fftSize];
            _re = new double[fftSize];
            _im = new double[fftSize];
            _ring = new float[fftSize];
            _db = new float[BinCount];

            // A full-scale sine reads 0 dBFS in its bin (see AudioSpectrumAnalyser).
            double sum = 0;
            for (int i = 0; i < fftSize; i++)
            {
                _window[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (fftSize - 1));
                sum += _window[i];
            }
            _scale = 2.0 / sum;
        }

        /// <summary>
        /// The latest frame in dBFS, one value per bin from 0 Hz upward. The
        /// buffer is reused: copy it before the next <see cref="Push"/>.
        /// </summary>
        public ReadOnlySpan<float> Levels => _db;

        /// <summary>
        /// Feeds samples. Calls <paramref name="onFrame"/> with <see cref="Levels"/>
        /// for every frame completed, which can be more than once per call.
        /// </summary>
        public void Push(ReadOnlySpan<float> samples, Action<ReadOnlySpan<float>> onFrame)
        {
            foreach (float s in samples)
            {
                // The newest FftSize samples, in a ring: _write is the oldest.
                _ring[_write] = s;
                _write = (_write + 1) % FftSize;
                if (_filled < FftSize) _filled++;
                _sinceLast++;
                if (_filled < FftSize || _sinceLast < HopSize) continue;
                _sinceLast = 0;
                Analyse();
                onFrame(_db);
            }
        }

        public void Reset()
        {
            _filled = 0;
            _write = 0;
            _sinceLast = 0;
            Array.Clear(_db);
        }

        private void Analyse()
        {
            for (int i = 0; i < FftSize; i++)
            {
                _re[i] = _ring[(_write + i) % FftSize] * _window[i];
                _im[i] = 0;
            }
            Fft.Transform(_re, _im);
            for (int k = 0; k < BinCount; k++)
            {
                double mag = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) * _scale;
                _db[k] = (float)(20.0 * Math.Log10(Math.Max(mag, 1e-9)));
            }
        }

        /// <summary>
        /// A level as one byte for the browser: 0 at -140 dBFS, 255 at -20 dBFS,
        /// so about half a dB a step. The page stretches its own colours.
        /// </summary>
        public static byte ToByte(float db) =>
            (byte)Math.Clamp(Math.Round((db + 140.0) * 255.0 / 120.0), 0, 255);
    }
}
