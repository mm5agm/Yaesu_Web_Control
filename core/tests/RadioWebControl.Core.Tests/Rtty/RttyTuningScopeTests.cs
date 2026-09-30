using System;
using System.Linq;
using RadioWebControl.Core.Services.Rtty;
using Xunit;
using Xunit.Abstractions;

namespace RadioWebControl.Core.Tests.Rtty
{
    /// <summary>
    /// What the operator reads off a crossed-ellipse scope is shape: a flat
    /// line for mark, an upright one for space, and arms that lean and open
    /// when the signal is off tune. So the tests measure shape - how much of
    /// the figure lies along X against along Y - and not decoded text.
    /// </summary>
    public class RttyTuningScopeTests
    {
        private const int    Rate  = 48000;
        private const double Mark  = 2125.0;
        private const double Space = 2295.0;

        private readonly ITestOutputHelper _out;
        public RttyTuningScopeTests(ITestOutputHelper output) => _out = output;

        private static float[] Tone(double hz, double seconds = 0.5, double amplitude = 0.3)
        {
            var a = new float[(int)(Rate * seconds)];
            for (int n = 0; n < a.Length; n++)
                a[n] = (float)(amplitude * Math.Sin(2.0 * Math.PI * hz * n / Rate));
            return a;
        }

        /// <summary>RMS of the X and Y traces over the last 50 ms, after settling.</summary>
        private static (double x, double y) Arms(float[] audio, double mark = Mark, double space = Space, bool limiter = true)
        {
            var scope = new RttyTuningScope(Rate, mark, space, limiter: limiter);
            scope.Process(audio);
            var p = scope.Snapshot(scope.PointRate / 20).Points;
            double sx = 0, sy = 0;
            for (int k = 0; k < p.Length; k += 2) { sx += p[k] * p[k]; sy += p[k + 1] * p[k + 1]; }
            int n = p.Length / 2;
            return (Math.Sqrt(sx / n), Math.Sqrt(sy / n));
        }

        private static double Db(double ratio) => 20.0 * Math.Log10(ratio);

        [Fact]
        public void MarkDrawsAFlatLine()
        {
            var (x, y) = Arms(Tone(Mark));
            _out.WriteLine($"mark tone: x {x:F4} y {y:F4}, {Db(x / y):F1} dB");
            Assert.True(Db(x / y) > 15, $"space arm only {Db(x / y):F1} dB below mark");
        }

        [Fact]
        public void WithoutTheLimiterTheArmIsAsLongAsTheToneIsLoud()
        {
            // 0 dB at the centre.
            var (x, _) = Arms(Tone(Mark), limiter: false);
            Assert.InRange(x, 0.3 / Math.Sqrt(2) * 0.9, 0.3 / Math.Sqrt(2) * 1.1);
        }

        [Fact]
        public void TheLimiterHoldsTheFigureTheSameSizeThroughAFade()
        {
            // 40 dB of fade must not shrink the cross: that is what kept the
            // old terminal-unit display crisp.
            var (loud, _)  = Arms(Tone(Mark, amplitude: 0.3));
            var (faded, _) = Arms(Tone(Mark, amplitude: 0.003));
            _out.WriteLine($"arm loud {loud:F4}, 40 dB down {faded:F4}");
            Assert.InRange(Db(faded / loud), -1.0, 1.0);
        }

        [Fact]
        public void TheLimiterMakesUnequalTonesDrawEqualArms()
        {
            // Selective fading often leaves one tone well below the other.
            var (m, _) = Arms(Tone(Mark, amplitude: 0.3));
            var (_, s) = Arms(Tone(Space, amplitude: 0.03));
            Assert.InRange(Db(s / m), -1.5, 1.5);
        }

        [Fact]
        public void SpaceDrawsAnUprightLine()
        {
            var (x, y) = Arms(Tone(Space));
            _out.WriteLine($"space tone: x {x:F4} y {y:F4}, {Db(y / x):F1} dB");
            Assert.True(Db(y / x) > 15, $"mark arm only {Db(y / x):F1} dB below space");
        }

        [Fact]
        public void HalfwayBetweenTheTonesTheFigureLeansAtFortyFiveDegrees()
        {
            // Mistuned by half the shift: both tones fall between the filters
            // and each passes equally, which is the fat diagonal ellipse.
            var (x, y) = Arms(Tone((Mark + Space) / 2));
            _out.WriteLine($"midway: x {x:F4} y {y:F4}");
            Assert.InRange(Db(x / y), -1.0, 1.0);
        }

        [Fact]
        public void AToneWellAwayBarelyDrawsAnything()
        {
            // 400 Hz off: the operator is nowhere near and the scope should say
            // so by showing next to nothing, not a confident shape.
            var (onX, _) = Arms(Tone(Mark), limiter: false);
            var (x, y) = Arms(Tone(Mark - 400), limiter: false);
            _out.WriteLine($"400 Hz off: x {Db(x / onX):F1} dB, y {Db(y / onX):F1} dB relative to on tune");
            Assert.True(Db(x / onX) < -20);
            Assert.True(Db(y / onX) < -20);
        }

        [Fact]
        public void ShiftIsFollowedWhenTheTonesMove()
        {
            // 850 Hz shift, as some commercial RTTY still uses.
            var (x, y) = Arms(Tone(Mark + 850), Mark, Mark + 850);
            Assert.True(Db(y / x) > 15);
        }

        [Fact]
        public void LevelsReportWhichFilterTheSignalIsIn()
        {
            var scope = new RttyTuningScope(Rate, Mark, Space);
            scope.Process(Tone(Mark));
            var f = scope.Snapshot(0);
            _out.WriteLine($"mark {f.MarkDb:F1} space {f.SpaceDb:F1} input {f.InputDb:F1} dBFS");
            Assert.Empty(f.Points);
            Assert.True(f.MarkDb - f.SpaceDb > 15);
            Assert.InRange(f.MarkDb - f.InputDb, -1.0, 1.0);
        }

        [Fact]
        public void SnapshotIsTheMostRecentPointsOldestFirst()
        {
            var scope = new RttyTuningScope(Rate, Mark, Space);
            scope.Process(new float[100]);
            Assert.Equal(50 * 2, scope.Snapshot(1000).Points.Length);   // half rate, fewer than asked
            scope.Process(Tone(Mark, 1.0));                              // wraps the ring
            Assert.Equal(RttyTuningScope.RingPoints * 2, scope.Snapshot(int.MaxValue).Points.Length);

            // The newest end is the settled tone, so it carries signal.
            var p = scope.Snapshot(10).Points;
            Assert.Contains(p.Where((_, i) => i % 2 == 0), v => Math.Abs(v) > 0.05f);
        }

        [Fact]
        public void TonesOutsideTheAudioBandAreRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RttyTuningScope(Rate, 0, Space));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RttyTuningScope(Rate, Mark, Rate));
        }
    }
}
