using RadioWebControl.Core.Services.Rtty;
using Xunit;

namespace RadioWebControl.Core.Tests.Rtty
{
    /// <summary>
    /// Two things are pinned down here, and both of them are things a comment
    /// cannot hold on to: the sign, which if wrong moves the radio away from the
    /// signal by twice the error, and the three refusals, which are the difference
    /// between a feature that tunes the radio and a feature that grabs it.
    /// </summary>
    public class RttyMarkCentreTests
    {
        // The dial reads the mark, and RTTY normal on this radio is the
        // lower-sideband case: audio rises as the dial rises. So a mark heard
        // above where it is wanted means the dial is above the signal, and the
        // dial has to come down.
        [Fact]
        public void Moves_the_dial_down_when_the_mark_is_heard_high()
        {
            var offset = RttyMarkCentre.ComputeOffsetHz(
                measuredMarkHz: 2191, wantedMarkHz: 2125, lowerSideband: true);

            Assert.Equal(-66, offset);
        }

        [Fact]
        public void Moves_the_dial_up_when_the_mark_is_heard_low()
        {
            var offset = RttyMarkCentre.ComputeOffsetHz(
                measuredMarkHz: 1950, wantedMarkHz: 2125, lowerSideband: true);

            Assert.Equal(175, offset);
        }

        [Fact]
        public void Reverses_on_the_upper_sideband()
        {
            var offset = RttyMarkCentre.ComputeOffsetHz(
                measuredMarkHz: 2191, wantedMarkHz: 2125, lowerSideband: false);

            Assert.Equal(66, offset);
        }

        // DDK9 as Colin left it on 2026-10-08: dial 10.100647 against a proper
        // 10.100998, mark heard at 2191 instead of 2125. A 350 Hz error on the
        // dial and a 66 Hz error in the audio are the same error - the rest of
        // the 350 is the tuner having been started on a mark the signal was never
        // sent on - so this checks only the part the analyser can see.
        [Fact]
        public void Corrects_the_bench_mistuning_on_DDK9()
        {
            var offset = RttyMarkCentre.ComputeOffsetHz(
                measuredMarkHz: 2191, wantedMarkHz: 2125,
                lowerSideband: true, confidence: 0.67);

            Assert.NotNull(offset);
            Assert.Equal(10_100_647 - 66, 10_100_647 + offset!.Value);
        }

        [Fact]
        public void Does_nothing_when_the_mark_is_already_close_enough()
        {
            // 20 Hz is under two analyser bins, so it is as likely to be
            // measurement error as mistuning.
            Assert.Null(RttyMarkCentre.ComputeOffsetHz(
                measuredMarkHz: 2145, wantedMarkHz: 2125, lowerSideband: true));
        }

        [Fact]
        public void Does_nothing_when_the_measurement_is_not_confident()
        {
            Assert.Null(RttyMarkCentre.ComputeOffsetHz(
                measuredMarkHz: 1800, wantedMarkHz: 2125,
                lowerSideband: true, confidence: 0.39));
        }

        [Fact]
        public void Does_nothing_when_the_signal_is_too_far_away_to_be_the_same_one()
        {
            Assert.Null(RttyMarkCentre.ComputeOffsetHz(
                measuredMarkHz: 1200, wantedMarkHz: 2125, lowerSideband: true));
        }

        [Fact]
        public void Does_nothing_with_a_tone_that_is_not_a_tone()
        {
            Assert.Null(RttyMarkCentre.ComputeOffsetHz(
                measuredMarkHz: 0, wantedMarkHz: 2125, lowerSideband: true));
            Assert.Null(RttyMarkCentre.ComputeOffsetHz(
                measuredMarkHz: double.NaN, wantedMarkHz: 2125, lowerSideband: true));
        }

        [Theory]
        [InlineData("RTTY-L", true)]
        [InlineData("rtty-l", true)]
        [InlineData("RTTY", true)]
        [InlineData("RTTY-U", false)]
        public void Knows_the_two_FSK_modes_and_which_way_each_inverts(string mode, bool lower)
        {
            Assert.Equal(lower, RttyMarkCentre.SidebandForFskMode(mode));
        }

        // AFSK is a supported way to run the tuner and deliberately not a
        // supported way to have the VFO moved: the operator's own software is
        // making the tones and has its own tuning indicator.
        [Theory]
        [InlineData("DATA-L")]
        [InlineData("DATA-U")]
        [InlineData("USB")]
        [InlineData("LSB")]
        [InlineData("CW-U")]
        [InlineData("FM")]
        [InlineData("")]
        [InlineData(null)]
        public void Refuses_every_mode_that_is_not_the_radios_own_FSK(string? mode)
        {
            Assert.Null(RttyMarkCentre.SidebandForFskMode(mode));
        }

        // ─── the passband cap ────────────────────────────────────────────────
        //
        // Added after the bench on 2026-10-09, where a 619 Hz offset through a
        // 1200 Hz filter was refused by the flat 500 Hz limit while the signal
        // was audibly decoding. These pin the shape of the replacement: a wide
        // filter may ask for more, and nothing may ask for less.

        [Fact]
        public void A_wide_filter_allows_a_move_the_old_flat_cap_refused()
        {
            // The real case: DDK9 at 2744 against 2125 wanted, 619 Hz away,
            // through the 1200 Hz filter it was decoding through.
            Assert.Null(RttyMarkCentre.ComputeOffsetHz(
                measuredMarkHz: 2744, wantedMarkHz: 2125, lowerSideband: true));

            Assert.Equal(-619, RttyMarkCentre.ComputeOffsetHz(
                measuredMarkHz: 2744, wantedMarkHz: 2125, lowerSideband: true,
                maxOffsetHz: 1200));
        }

        // The guard can only ever be loosened by this parameter. A caller
        // reporting a narrow filter must not be able to make the function
        // stricter than it was when 500 Hz was the only rule - that case is
        // answered by advising the operator, not by quietly giving up.
        [Theory]
        [InlineData(null)]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(50.0)]
        [InlineData(250.0)]
        [InlineData(500.0)]
        public void Nothing_can_tighten_the_guard_below_the_old_constant(double? cap)
        {
            // 400 Hz: inside the old constant, so it must still be allowed
            // however narrow the caller claims its filter is.
            Assert.Equal(-400, RttyMarkCentre.ComputeOffsetHz(
                measuredMarkHz: 2525, wantedMarkHz: 2125, lowerSideband: true,
                maxOffsetHz: cap));
        }

        [Fact]
        public void Still_refuses_a_tone_further_off_than_the_passband_is_wide()
        {
            // 1300 Hz away through a 1200 Hz filter: it cannot have been the
            // signal in the passband, so it was something else.
            Assert.Null(RttyMarkCentre.ComputeOffsetHz(
                measuredMarkHz: 3425, wantedMarkHz: 2125, lowerSideband: true,
                maxOffsetHz: 1200));
        }

        // Widening the cap must not weaken either of the other two refusals.
        [Fact]
        public void A_wide_filter_does_not_excuse_a_poor_measurement()
        {
            Assert.Null(RttyMarkCentre.ComputeOffsetHz(
                measuredMarkHz: 2744, wantedMarkHz: 2125, lowerSideband: true,
                confidence: 0.2, maxOffsetHz: 2400));
        }

        [Fact]
        public void A_wide_filter_does_not_make_it_chase_a_tone_already_on_target()
        {
            Assert.Null(RttyMarkCentre.ComputeOffsetHz(
                measuredMarkHz: 2135, wantedMarkHz: 2125, lowerSideband: true,
                maxOffsetHz: 2400));
        }

        [Fact]
        public void Keeps_the_sign_right_at_the_wider_distances_too()
        {
            // Heard high through a wide filter: still comes down.
            Assert.Equal(-900, RttyMarkCentre.ComputeOffsetHz(
                measuredMarkHz: 3025, wantedMarkHz: 2125, lowerSideband: true,
                maxOffsetHz: 2400));

            // Heard low through a wide filter: still goes up.
            Assert.Equal(900, RttyMarkCentre.ComputeOffsetHz(
                measuredMarkHz: 1225, wantedMarkHz: 2125, lowerSideband: true,
                maxOffsetHz: 2400));

            // And the upper-sideband case inverts, as before.
            Assert.Equal(900, RttyMarkCentre.ComputeOffsetHz(
                measuredMarkHz: 3025, wantedMarkHz: 2125, lowerSideband: false,
                maxOffsetHz: 2400));
        }
    }
}
