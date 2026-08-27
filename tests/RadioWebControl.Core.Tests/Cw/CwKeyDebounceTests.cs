using RadioWebControl.Core.Services.Cw;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// A mark that dips below the off threshold for a hop or two is reported as
    /// two marks with a gap between them, and the element decoder cannot tell
    /// that from a genuine dit-dit. On the bench this is what separates the
    /// files that decode from the files that produce E/I/S chatter, so the
    /// suppression and - just as importantly - the backdating that keeps it
    /// from costing timing accuracy are pinned down here.
    /// </summary>
    public class CwKeyDebounceTests
    {
        private const int Rate  = 48000;
        private const double Pitch = 600.0;

        /// <summary>Start and length in ms of every key-down run the detector reports.</summary>
        private static List<(double StartMs, double LengthMs)> Marks(float[] audio, double debounceMs)
        {
            var detector = new CwToneDetector(new CwToneDetectorOptions
            {
                InputSampleRate = Rate,
                PitchHz         = Pitch,
                TrackPitch      = false,
                KeyDebounceMs   = debounceMs,
            });

            var samples = new List<CwToneSample>();
            detector.Process(audio, samples);
            detector.Flush(samples);

            var marks = new List<(double, double)>();
            bool  down = false;
            double edge = 0.0;
            foreach (var s in samples)
            {
                if (s.KeyDown && !down) { down = true;  edge = s.TimeSeconds; }
                else if (!s.KeyDown && down)
                {
                    down = false;
                    marks.Add((edge * 1000.0, (s.TimeSeconds - edge) * 1000.0));
                }
            }
            return marks;
        }

        /// <summary>A dah at 20 wpm - 180 ms - with an optional notch cut out of the middle.</summary>
        private static float[] Dah(double notchMs)
        {
            var gen   = new CwSignalGenerator();
            var audio = CwSignalGenerator.Concat(gen.LeadIn(), gen.Generate("T", 20.0), gen.Silence(0.5));

            if (notchMs > 0)
            {
                // The dah starts one second in. Punch the notch through its
                // centre, which is where a real dropout does the most damage:
                // far enough from both edges that neither can mask it.
                int centre = (int)(1.09 * Rate);
                int half   = (int)(notchMs * Rate / 2000.0);
                for (int i = centre - half; i < centre + half; i++) audio[i] = 0f;
            }
            return audio;
        }

        [Fact]
        public void A_dropout_splits_one_mark_into_two_when_the_debounce_is_off()
        {
            var marks = Marks(Dah(notchMs: 10.0), debounceMs: 0.0);
            Assert.True(marks.Count >= 2, $"expected the notch to fragment the dah, got {marks.Count} mark(s)");
        }

        [Fact]
        public void The_debounce_puts_the_fragments_back_together()
        {
            var marks = Marks(Dah(notchMs: 10.0), debounceMs: 10.0);
            Assert.Single(marks);
        }

        [Fact]
        public void Suppressing_a_dropout_does_not_move_the_edges()
        {
            // Backdating is the whole point: a plain hold-off would confirm the
            // transition late and report every mark two hops long.
            var clean  = Marks(Dah(notchMs: 0.0),  debounceMs: 10.0);
            var healed = Marks(Dah(notchMs: 10.0), debounceMs: 10.0);

            Assert.Single(clean);
            Assert.Single(healed);
            Assert.InRange(healed[0].StartMs  - clean[0].StartMs,  -6.0, 6.0);
            Assert.InRange(healed[0].LengthMs - clean[0].LengthMs, -6.0, 6.0);
        }

        [Fact]
        public void A_real_element_space_is_not_swallowed()
        {
            // 10 ms of suppression must not merge dit-space-dit at 40 wpm, where
            // the space is 30 ms. This is the constraint that caps how large the
            // debounce can safely be.
            var gen   = new CwSignalGenerator();
            var audio = CwSignalGenerator.Concat(gen.LeadIn(), gen.Generate("I", 40.0), gen.Silence(0.5));

            Assert.Equal(2, Marks(audio, debounceMs: 10.0).Count);
        }

        [Fact]
        public void On_by_default_at_ten_milliseconds()
        {
            Assert.Equal(10.0, new CwToneDetectorOptions().KeyDebounceMs);
            Assert.Equal(10.0, new CwDecoderOptions().KeyDebounceMs);
        }
    }
}
