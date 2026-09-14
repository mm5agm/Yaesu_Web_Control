using RadioWebControl.Core.Services.Cw;
using Xunit.Abstractions;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// The readability gate: can what is arriving be Morse at all?
    ///
    /// This exists because of bench/probe15.wav, thirty seconds of 15 m taken
    /// on 2026-08-26. The tone detector chattered across its hysteresis on a
    /// near-threshold carrier, every chatter became a mark, and the element
    /// decoder faithfully counted them into 160 characters of E I S H 5 - at
    /// 60 WPM, which is the MaxWpm clamp, and with confidence 1.00 once the
    /// tone was pinned. Confidence was not wrong; it measures tone tracking,
    /// and the tone really was where it said. The text was still worthless,
    /// and an operator cannot tell that stream from a bad copy of a real
    /// station.
    /// </summary>
    public class CwReadabilityTests
    {
        private readonly ITestOutputHelper _out;

        public CwReadabilityTests(ITestOutputHelper output) => _out = output;

        private static (string Text, CwDecoderEngine Engine) Run(float[] audio, double pitchHz = 600.0)
        {
            var engine = new CwDecoderEngine(new CwDecoderOptions { PitchHz = pitchHz });
            var text = new System.Text.StringBuilder();

            const int frame = 480;
            for (int i = 0; i < audio.Length; i += frame)
            {
                int n = Math.Min(frame, audio.Length - i);
                text.Append(engine.ProcessFrame(audio.AsSpan(i, n)));
            }
            text.Append(engine.Flush());

            return (text.ToString(), engine);
        }

        /// <summary>
        /// Short marks of one fixed length, sparsely spaced - the shape the
        /// fault actually had. Measured on bench/probe15.wav: 131 marks in
        /// 30 s at a 15 ms median, with the key down for only 7.9% of the
        /// file, so the gaps run to roughly 200 ms.
        ///
        /// The spacing matters. A 50% duty square wave at 15 ms is not this:
        /// the detector envelope cannot follow it, sees one continuous tone,
        /// and produces almost no marks at all.
        /// </summary>
        private static float[] Chatter(double markMs, double gapMs, double seconds,
                                       double toneHz = 600.0, int rate = 48000)
        {
            var audio = new float[(int)(seconds * rate)];
            int mark = Math.Max(1, (int)(markMs / 1000.0 * rate));
            int period = mark + Math.Max(1, (int)(gapMs / 1000.0 * rate));

            for (int i = 0; i < audio.Length; i++)
            {
                if (i % period >= mark) continue;
                audio[i] = (float)(0.25 * Math.Sin(2.0 * Math.PI * toneHz * i / rate));
            }

            return audio;
        }

        [Fact]
        public void RealMorseReadsAsReadable()
        {
            var gen = new CwSignalGenerator { ToneHz = 600.0 };
            var audio = CwSignalGenerator.Concat(gen.LeadIn(),
                                                 gen.Generate("CQ CQ DE MM5AGM MM5AGM K", 20));

            var (text, engine) = Run(audio);

            _out.WriteLine($"readability {engine.Readability}, spread {engine.MarkSpread:F2}");
            _out.WriteLine($"text: {text}");

            Assert.Equal(CwReadability.Readable, engine.Readability);

            // Morse has two mark lengths in a 3:1 ratio, so the spread lands
            // near 3 whatever the speed. That speed independence is the point:
            // the WPM estimate is one of the things that fails first.
            Assert.InRange(engine.MarkSpread, 2.0, 8.0);
        }

        [Fact]
        public void GatingDoesNotEatTheOpeningCharacters()
        {
            // The gate cannot judge until it has seen ReadabilityMinMarks, so
            // text decoded before then is held rather than dropped. If the
            // holding were wrong, a readable signal would lose its callsign.
            var gen = new CwSignalGenerator { ToneHz = 600.0 };
            var audio = CwSignalGenerator.Concat(gen.LeadIn(),
                                                 gen.Generate("CQ CQ DE MM5AGM MM5AGM K", 20));

            var (text, _) = Run(audio);

            _out.WriteLine($"text: {text}");
            Assert.StartsWith("CQ", text.TrimStart());
        }

        [Theory]
        [InlineData(15.0)]
        [InlineData(25.0)]
        [InlineData(40.0)]
        public void OneMarkLengthReadsAsChatter(double markMs)
        {
            var (text, engine) = Run(Chatter(markMs, gapMs: 200.0, seconds: 20.0));

            _out.WriteLine($"{markMs:F0} ms marks: readability {engine.Readability}, "
                         + $"spread {engine.MarkSpread:F2}, {text.Length} characters");

            Assert.Equal(CwReadability.Chatter, engine.Readability);
        }

        [Fact]
        public void ChatterProducesNoText()
        {
            // The fault as the operator met it: a stream of E I S H 5 that
            // looks like a bad copy of a real station. Nothing must come out.
            var (text, engine) = Run(Chatter(markMs: 15.0, gapMs: 200.0, seconds: 30.0));

            _out.WriteLine($"readability {engine.Readability}, spread {engine.MarkSpread:F2}");
            _out.WriteLine($"text: [{text}]");

            Assert.Equal(CwReadability.Chatter, engine.Readability);
            Assert.Equal(string.Empty, text);
        }

        [Fact]
        public void NothingIsJudgedBeforeThereIsEvidence()
        {
            // Two seconds of silence is not "unreadable", it is "no idea yet".
            // The distinction matters: an application showing "nothing
            // readable" the instant it starts would be lying.
            var gen = new CwSignalGenerator { ToneHz = 600.0 };
            var (_, engine) = Run(gen.Silence(2.0));

            Assert.Equal(CwReadability.Unknown, engine.Readability);
        }
    }
}
