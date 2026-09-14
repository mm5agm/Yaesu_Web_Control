using System;
using System.Collections.Generic;
using System.Linq;
using RadioWebControl.Core.Services.Cw;
using Xunit;
using Xunit.Abstractions;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// The phasor is a tuning aid, so what has to be true of it is geometric,
    /// not statistical: on frequency it stands still, off frequency it turns,
    /// and it turns at the tuning error in the direction of the error. Those
    /// three things are what an operator reads off the screen, so those three
    /// things are what is tested here.
    ///
    /// Named for the RTTY crossed-ellipse scope this imitates - tune until the
    /// figure stops moving.
    /// </summary>
    public class CwPhasorTests
    {
        private const int    Rate  = 48000;
        private const double Pitch = 600.0;

        private readonly ITestOutputHelper _out;
        public CwPhasorTests(ITestOutputHelper output) => _out = output;

        /// <summary>A steady key-down tone, which is what the operator holds while tuning.</summary>
        private static float[] Carrier(double toneHz, double seconds, double amplitude = 0.3)
        {
            var a = new float[(int)(Rate * seconds)];
            for (int n = 0; n < a.Length; n++)
                a[n] = (float)(amplitude * Math.Sin(2.0 * Math.PI * toneHz * n / Rate));
            return a;
        }

        private static List<CwToneSample> Run(float[] audio, double pitchHz = Pitch)
        {
            var det = new CwToneDetector(new CwToneDetectorOptions
            {
                InputSampleRate = Rate,
                PitchHz         = pitchHz,
                TrackPitch      = false,   // the aid answers "where is it", not "follow it"
            });
            var outp = new List<CwToneSample>();
            det.Process(audio, outp);
            det.Flush(outp);
            return outp;
        }

        /// <summary>
        /// Unwrapped angle against time, least squares. The slope is turns per
        /// second, which is the tuning error the operator is trying to null.
        /// </summary>
        private static double TurnsPerSecond(List<CwToneSample> s)
        {
            var pts = s.Where(x => Math.Sqrt(x.PhasorI * x.PhasorI + x.PhasorQ * x.PhasorQ) > 1e-4)
                       .Skip(20).ToList();
            Assert.True(pts.Count > 50, $"only {pts.Count} usable phasor points");

            double prev = 0, unwrapped = 0;
            var t = new List<double>();
            var y = new List<double>();
            for (int k = 0; k < pts.Count; k++)
            {
                double a = Math.Atan2(pts[k].PhasorQ, pts[k].PhasorI);
                if (k == 0) { prev = a; unwrapped = a; }
                else
                {
                    double d = a - prev;
                    while (d >  Math.PI) d -= 2.0 * Math.PI;
                    while (d < -Math.PI) d += 2.0 * Math.PI;
                    unwrapped += d;
                    prev = a;
                }
                t.Add(pts[k].TimeSeconds);
                y.Add(unwrapped);
            }

            double mt = t.Average(), my = y.Average();
            double num = 0, den = 0;
            for (int k = 0; k < t.Count; k++)
            {
                num += (t[k] - mt) * (y[k] - my);
                den += (t[k] - mt) * (t[k] - mt);
            }
            return num / den / (2.0 * Math.PI);
        }

        [Theory]
        [InlineData(   0.0)]   // zero beat: the figure must stand still
        [InlineData( +20.0)]
        [InlineData( -20.0)]
        [InlineData(  +5.0)]   // the fine end, where an operator finishes the job
        [InlineData( -50.0)]
        public void The_phasor_turns_at_the_tuning_error(double offsetHz)
        {
            var s        = Run(Carrier(Pitch + offsetHz, 2.0));
            double turns = TurnsPerSecond(s);

            _out.WriteLine($"  tone {Pitch + offsetHz:F0} Hz against a {Pitch:F0} Hz pitch: " +
                           $"{turns:+0.00;-0.00} turns/s (want {offsetHz:+0.00;-0.00})");
            Assert.InRange(turns, offsetHz - 0.6, offsetHz + 0.6);
        }

        /// <summary>
        /// The direction has to be honest, or the aid sends the operator the
        /// wrong way round the dial - worse than no aid at all.
        /// </summary>
        [Fact]
        public void Above_the_pitch_and_below_it_turn_opposite_ways()
        {
            double up   = TurnsPerSecond(Run(Carrier(Pitch + 15.0, 2.0)));
            double down = TurnsPerSecond(Run(Carrier(Pitch - 15.0, 2.0)));

            _out.WriteLine($"  +15 Hz -> {up:+0.0;-0.0} turns/s, -15 Hz -> {down:+0.0;-0.0} turns/s");
            Assert.True(up > 0,   $"a tone above the pitch turned {up:+0.0;-0.0}");
            Assert.True(down < 0, $"a tone below the pitch turned {down:+0.0;-0.0}");
        }

        /// <summary>
        /// On frequency the angle must be genuinely steady, not merely slow.
        /// A drift of a few degrees a second still smears the figure over the
        /// seconds an operator spends looking at it.
        /// </summary>
        [Fact]
        public void On_frequency_the_figure_holds_still()
        {
            var s = Run(Carrier(Pitch, 3.0));
            var pts = s.Skip(40).Where(x => Math.Sqrt(x.PhasorI * x.PhasorI + x.PhasorQ * x.PhasorQ) > 1e-4)
                       .ToList();

            double a0 = Math.Atan2(pts[0].PhasorQ, pts[0].PhasorI);
            double worst = pts.Max(x =>
            {
                double d = Math.Atan2(x.PhasorQ, x.PhasorI) - a0;
                while (d >  Math.PI) d -= 2.0 * Math.PI;
                while (d < -Math.PI) d += 2.0 * Math.PI;
                return Math.Abs(d);
            }) * 180.0 / Math.PI;

            _out.WriteLine($"  over {pts.Count} hops the angle wandered at most {worst:F1} degrees");
            Assert.True(worst < 5.0, $"the figure drifted {worst:F1} degrees on frequency");
        }

        /// <summary>
        /// The radius carries the level, so key-up has to collapse to the
        /// middle - otherwise the trail keeps drawing between characters and
        /// the figure never resolves.
        /// </summary>
        [Fact]
        public void Silence_collapses_the_phasor_to_the_origin()
        {
            var audio = new float[Rate];                       // one second of nothing
            var s     = Run(audio);
            double r  = s.Skip(20).Max(x => Math.Sqrt(x.PhasorI * x.PhasorI + x.PhasorQ * x.PhasorQ));

            _out.WriteLine($"  silence gave a largest radius of {r:E2}");
            Assert.True(r < 1e-6, $"silence drew a radius of {r:E2}");
        }
    }

    /// <summary>
    /// The ring the browser drains. Its job is to hand over every point once,
    /// in order, and to fail safely rather than replay stale audio when a
    /// display stops polling.
    /// </summary>
    public class CwPhasorRingTests
    {
        private const int Rate = 48000;

        private static CwDecoderEngine Feed(double seconds, out int frames)
        {
            var eng = new CwDecoderEngine(new CwDecoderOptions
            {
                InputSampleRate = Rate,
                PitchHz         = 600.0,
                TrackPitch      = false,
            });

            int total = (int)(Rate * seconds);
            var audio = new float[total];
            for (int n = 0; n < total; n++)
                audio[n] = (float)(0.3 * Math.Sin(2.0 * Math.PI * 615.0 * n / Rate));

            const int frame = 480;
            frames = 0;
            for (int i = 0; i + frame <= total; i += frame)
            {
                eng.ProcessFrame(audio.AsSpan(i, frame));
                frames++;
            }
            return eng;
        }

        [Fact]
        public void Draining_from_the_cursor_hands_over_each_point_once()
        {
            var eng = Feed(1.0, out _);

            var first = eng.PhasorSince(0, out long c1);
            Assert.NotEmpty(first);

            // Nothing new since, so nothing comes back and the cursor holds.
            var none = eng.PhasorSince(c1, out long c2);
            Assert.Empty(none);
            Assert.Equal(c1, c2);
        }

        [Fact]
        public void A_display_that_stops_polling_skips_the_gap_rather_than_replaying_it()
        {
            // Five seconds at 200 hops/s is ~1000 points through a 512 ring.
            var eng = Feed(5.0, out _);

            var all = eng.PhasorSince(0, out long cursor);
            Assert.True(all.Length <= 512, $"handed back {all.Length} points from a 512 ring");
            Assert.True(cursor > 512, $"cursor {cursor} should have run past the ring");
        }

        [Fact]
        public void A_cursor_from_the_future_is_not_trusted()
        {
            var eng = Feed(1.0, out _);
            var pts = eng.PhasorSince(long.MaxValue / 2, out _);
            Assert.Empty(pts);
        }
    }
}
