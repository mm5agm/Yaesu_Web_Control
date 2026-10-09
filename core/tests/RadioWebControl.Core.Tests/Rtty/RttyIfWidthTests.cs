using RadioWebControl.Core.Services.Rtty;
using Xunit;

namespace RadioWebControl.Core.Tests.Rtty
{
    /// <summary>
    /// The formula is checked against the filters radios actually ship with,
    /// because that is the only independent evidence available for it, and the
    /// widen-only rule is checked directly, because it is the part a later change
    /// could break without anything looking wrong.
    /// </summary>
    public class RttyIfWidthTests
    {
        // 170 at 45.45 is the amateur standard, and every rig built for it offers
        // a filter around 250-300 Hz. An arithmetic that landed well outside that
        // range would be the arithmetic that was wrong, not the radios.
        [Fact]
        public void Agrees_with_the_filter_every_RTTY_radio_ships()
        {
            Assert.Equal(261, RttyIfWidth.MinimumHz(170, 45.45));
        }

        [Fact]
        public void Needs_more_room_for_a_wide_shift()
        {
            // DDK9: 450 Hz shift at 50 baud. A 250 Hz CW filter cannot carry this
            // at all, which is the case the widen exists for.
            Assert.Equal(550, RttyIfWidth.MinimumHz(450, 50));
        }

        [Fact]
        public void Needs_more_room_for_a_fast_signal()
        {
            Assert.Equal(370, RttyIfWidth.MinimumHz(170, 100));
        }

        [Fact]
        public void Rounds_a_floor_upwards()
        {
            // 170 + 90.9 = 260.9, and a floor rounded down is a width that clips.
            Assert.True(RttyIfWidth.MinimumHz(170, 45.45) >= 170 + 2 * 45.45);
        }

        [Fact]
        public void Widens_a_filter_that_is_too_narrow()
        {
            Assert.Equal(550, RttyIfWidth.WidenToHz(currentWidthHz: 250, shiftHz: 450, baud: 50));
        }

        [Fact]
        public void Leaves_a_filter_alone_when_it_is_already_wide_enough()
        {
            Assert.Null(RttyIfWidth.WidenToHz(currentWidthHz: 600, shiftHz: 450, baud: 50));
        }

        // The whole point of the class. A 2700 Hz passband is a poor choice for
        // RTTY and the operator may well want it narrower - but that is their
        // call, and nothing here is allowed to make it for them.
        [Fact]
        public void Never_narrows_a_filter_that_is_wider_than_it_needs_to_be()
        {
            Assert.Null(RttyIfWidth.WidenToHz(currentWidthHz: 2700, shiftHz: 170, baud: 45.45));
        }

        [Fact]
        public void Writes_nothing_when_the_current_width_is_unknown()
        {
            Assert.Null(RttyIfWidth.WidenToHz(currentWidthHz: 0,  shiftHz: 450, baud: 50));
            Assert.Null(RttyIfWidth.WidenToHz(currentWidthHz: -1, shiftHz: 450, baud: 50));
        }

        [Fact]
        public void Writes_nothing_when_there_is_no_signal_to_size_a_filter_for()
        {
            Assert.Null(RttyIfWidth.WidenToHz(currentWidthHz: 250, shiftHz: 0, baud: 0));
        }
    }
}
