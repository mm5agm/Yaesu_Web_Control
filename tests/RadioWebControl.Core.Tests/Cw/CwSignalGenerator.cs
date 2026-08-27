using RadioWebControl.Core.Services.Cw;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// Generates synthetic Morse audio so the decoder can be measured rather than
    /// listened to. Known text, set speed, set signal-to-noise, and optionally a
    /// ragged fist or a slow fade.
    ///
    /// Signal-to-noise is quoted in a 500 Hz reference bandwidth, which is the
    /// convention every operator already reads on a band scope, rather than in the
    /// full 24 kHz of the sample rate, where the same number would mean something
    /// far harsher and the figures would not be comparable to anything.
    /// </summary>
    public sealed class CwSignalGenerator
    {
        public int    SampleRate  { get; init; } = 48000;
        public double ToneHz      { get; init; } = 600.0;
        public double RiseFallMs  { get; init; } = 5.0;
        public double Amplitude   { get; init; } = 0.5;

        /// <summary>Fractional random variation applied to every mark and gap. 0.2 is a bad fist.</summary>
        public double FistJitter  { get; init; } = 0.0;

        private readonly Random _rng;

        public CwSignalGenerator(int seed = 20260823) => _rng = new Random(seed);

        public float[] Silence(double seconds) => new float[(int)(seconds * SampleRate)];

        /// <summary>
        /// Audio before the transmission starts, which every capture has and
        /// which the detector needs: its noise floor is an EMA with a quarter
        /// second time constant, and it reports nothing keyed until that has
        /// settled (CwToneDetectorOptions.WarmupSeconds). A real decoder is
        /// switched on before the other operator starts sending; a test that
        /// begins keying in the first frame is the only place that does not
        /// happen.
        /// </summary>
        public float[] LeadIn() => Silence(1.0);

        /// <summary>Noise-free Morse at the given speed.</summary>
        public float[] Generate(string text, double wpm)
            => Generate(text, wpm, wpm);

        /// <summary>
        /// Noise-free Morse with Farnsworth spacing: elements sent at
        /// <paramref name="characterWpm"/>, gaps stretched so the whole
        /// transmission comes out at <paramref name="wpm"/>.
        ///
        /// This is how every code practice recording below walking pace is
        /// actually sent, and the decoder had never seen it. A learner at 5 WPM
        /// is not listening to 240 ms dits - the ARRL files send 85 ms dits, a
        /// hair under 15 WPM, and leave a second and a half between characters.
        /// A decoder that derives its character gap from its measured dit reads
        /// every one of those gaps as a word gap and returns each character as
        /// its own word.
        ///
        /// The stretch follows the usual PARIS bookkeeping: a word is 50 units,
        /// 31 of them keying and 19 of them spacing, and the extra time goes
        /// into the 19 in the same three-to-seven proportion, so the ratio
        /// between a character gap and a word gap stays at the textbook 2.33
        /// however far the two speeds are pulled apart.
        ///
        /// Worth knowing when comparing against W1AW: their files stretch the
        /// word gaps further still - measured 3.68 at 5 WPM, not 2.33 - so this
        /// generator is the easier of the two cases, not the harder one.
        /// </summary>
        public float[] Generate(string text, double wpm, double characterWpm)
        {
            if (characterWpm < wpm) characterWpm = wpm;

            double ditMs = 1200.0 / characterWpm;

            double charGapMs = ditMs * 3.0;
            double wordGapMs = ditMs * 7.0;
            if (characterWpm > wpm)
            {
                double totalDelayMs =
                    (60.0 * characterWpm - 37.2 * wpm) / (characterWpm * wpm) * 1000.0;
                charGapMs = 3.0 * totalDelayMs / 19.0;
                wordGapMs = 7.0 * totalDelayMs / 19.0;
            }

            var timeline = BuildTimeline(text, ditMs, charGapMs, wordGapMs);

            int total = 0;
            foreach (var (ms, _) in timeline) total += MsToSamples(ms);
            if (total == 0) return Array.Empty<float>();

            // Hard-keyed envelope first, then smoothed, which is easier to reason
            // about than trying to shape each edge as it is written.
            var env = new double[total];
            int pos = 0;
            foreach (var (ms, keyDown) in timeline)
            {
                int n = MsToSamples(ms);
                if (keyDown)
                    for (int i = 0; i < n && pos + i < total; i++) env[pos + i] = 1.0;
                pos += n;
            }

            Smooth(env, Math.Max(2, MsToSamples(RiseFallMs)));

            var outSamples = new float[total];
            double phase = 0.0;
            double step  = 2.0 * Math.PI * ToneHz / SampleRate;
            for (int i = 0; i < total; i++)
            {
                outSamples[i] = (float)(Amplitude * env[i] * Math.Sin(phase));
                phase += step;
                if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;
            }

            return outSamples;
        }

        /// <summary>
        /// Add white noise for the requested SNR in a 500 Hz bandwidth, in place.
        /// </summary>
        public void AddNoise(float[] samples, double snrDb)
        {
            double signalPower = Amplitude * Amplitude / 2.0;
            double refBw       = 500.0;
            double fullBw      = SampleRate / 2.0;
            double noisePower  = signalPower / Math.Pow(10.0, snrDb / 10.0) * (fullBw / refBw);
            double sigma       = Math.Sqrt(noisePower);

            for (int i = 0; i < samples.Length; i++)
                samples[i] += (float)(sigma * Gaussian());
        }

        /// <summary>Slow amplitude fade, in place: what QSB does to a signal.</summary>
        public void ApplyQsb(float[] samples, double fadeHz, double depthDb)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                double t  = (double)i / SampleRate;
                double dB = -0.5 * depthDb * (1.0 - Math.Cos(2.0 * Math.PI * fadeHz * t));
                samples[i] *= (float)Math.Pow(10.0, dB / 20.0);
            }
        }

        public static float[] Concat(params float[][] parts)
        {
            int total = 0;
            foreach (var p in parts) total += p.Length;
            var result = new float[total];
            int pos = 0;
            foreach (var p in parts) { Array.Copy(p, 0, result, pos, p.Length); pos += p.Length; }
            return result;
        }

        private List<(double Ms, bool KeyDown)> BuildTimeline(
            string text, double ditMs, double charGapMs, double wordGapMs)
        {
            var timeline = new List<(double, bool)>();
            string encoded = MorseTable.EncodeText(text);

            for (int i = 0; i < encoded.Length; i++)
            {
                char c = encoded[i];
                switch (c)
                {
                    case '.': timeline.Add((Jitter(ditMs), true)); break;
                    case '-': timeline.Add((Jitter(ditMs * 3.0), true)); break;
                    case ' ': timeline.Add((Jitter(charGapMs), false)); continue;
                    case '/': timeline.Add((Jitter(wordGapMs), false)); continue;
                }

                // Inter-element gap, unless the next thing is a separator.
                bool nextIsElement = i + 1 < encoded.Length
                                     && (encoded[i + 1] == '.' || encoded[i + 1] == '-');
                if (nextIsElement) timeline.Add((Jitter(ditMs), false));
            }

            return timeline;
        }

        private double Jitter(double ms)
            => FistJitter <= 0 ? ms : ms * (1.0 + FistJitter * (_rng.NextDouble() * 2.0 - 1.0));

        private int MsToSamples(double ms) => (int)Math.Round(ms * SampleRate / 1000.0);

        /// <summary>Convolve with a Hann window, which turns hard edges into raised cosines.</summary>
        private static void Smooth(double[] env, int length)
        {
            var kernel = new double[length];
            double sum = 0;
            for (int i = 0; i < length; i++)
            {
                kernel[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (length - 1));
                sum += kernel[i];
            }
            if (sum <= 0) return;
            for (int i = 0; i < length; i++) kernel[i] /= sum;

            var src = (double[])env.Clone();
            int half = length / 2;
            for (int i = 0; i < env.Length; i++)
            {
                double acc = 0;
                for (int k = 0; k < length; k++)
                {
                    int j = i + k - half;
                    if (j >= 0 && j < src.Length) acc += kernel[k] * src[j];
                }
                env[i] = acc;
            }
        }

        private double Gaussian()
        {
            double u1 = 1.0 - _rng.NextDouble();
            double u2 = _rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}
