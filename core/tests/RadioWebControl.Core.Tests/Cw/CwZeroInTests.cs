using RadioWebControl.Core.Services.Cw;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// The sign convention is the only thing here that is easy to get wrong, and
    /// getting it wrong on the Icom would move the VFO the wrong way by twice the
    /// error, so it is pinned down by test rather than by comment.
    /// </summary>
    public class CwZeroInTests
    {
        [Fact]
        public void Moves_the_VFO_up_when_the_tone_is_high_on_the_upper_sideband()
        {
            var offset = CwZeroIn.ComputeOffsetHz(measuredToneHz: 715, targetPitchHz: 600);
            Assert.Equal(115.0, offset);
        }

        [Fact]
        public void Moves_the_other_way_on_the_lower_sideband()
        {
            var offset = CwZeroIn.ComputeOffsetHz(measuredToneHz: 715, targetPitchHz: 600,
                                                  lowerSideband: true);
            Assert.Equal(-115.0, offset);
        }

        [Fact]
        public void Does_nothing_when_the_tone_is_already_right()
            => Assert.Equal(0.0, CwZeroIn.ComputeOffsetHz(600, 600));

        [Fact]
        public void Refuses_an_offset_larger_than_the_limit()
        {
            // Almost always the detector locking onto the wrong signal, and a
            // reader that yanks the VFO on one bad frame is worse than one that
            // sits still.
            Assert.Null(CwZeroIn.ComputeOffsetHz(2200, 600, maxOffsetHz: 500));
        }

        [Fact]
        public void Refuses_a_measurement_it_does_not_believe()
            => Assert.Null(CwZeroIn.ComputeOffsetHz(715, 600, confidence: 0.2));

        [Fact]
        public void Rejects_nonsense_input()
        {
            Assert.Null(CwZeroIn.ComputeOffsetHz(0, 600));
            Assert.Null(CwZeroIn.ComputeOffsetHz(715, 0));
            Assert.Null(CwZeroIn.ComputeOffsetHz(double.NaN, 600));
        }

        [Fact]
        public void Rounds_to_whole_Hz_because_that_is_all_CAT_takes()
            => Assert.Equal(115L, CwZeroIn.ComputeOffsetWholeHz(715.4, 600.0));
    }
}
