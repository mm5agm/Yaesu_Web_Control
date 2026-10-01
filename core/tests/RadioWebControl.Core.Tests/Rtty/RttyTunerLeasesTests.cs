using System;
using RadioWebControl.Core.Services.Rtty;
using Xunit;

namespace RadioWebControl.Core.Tests.Rtty
{
    /// <summary>
    /// The tuner open in two windows, one closing: the other must keep its
    /// figure. Before the leases, either window closing stopped both.
    /// </summary>
    public class RttyTunerLeasesTests
    {
        private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        private static DateTime At(double seconds) => T0.AddSeconds(seconds);

        [Fact]
        public void One_window_closing_leaves_the_other_running()
        {
            var l = new RttyTunerLeases();
            l.Start("main", At(0));
            l.Start("popout", At(0));

            l.Stop("main", At(1));
            l.Poll("popout", At(2));
            Assert.Null(l.Expire(At(4)));

            Assert.False(l.Holds("main"));
            Assert.True(l.Holds("popout"));
        }

        [Fact]
        public void The_last_window_closing_stops_after_the_debounce()
        {
            var l = new RttyTunerLeases();
            l.Start("main", At(0));
            l.Stop("main", At(1));

            Assert.Null(l.Expire(At(2.5)));
            Assert.Equal("dialog closed", l.Expire(At(3)));
            Assert.Equal(0, l.Count);
        }

        [Fact]
        public void Reopening_inside_the_debounce_keeps_the_lease()
        {
            var l = new RttyTunerLeases();
            l.Start("main", At(0));
            l.Stop("main", At(1));
            l.Start("main", At(2));

            Assert.Null(l.Expire(At(10)));
            Assert.True(l.Holds("main"));
        }

        [Fact]
        public void A_window_that_stops_polling_lapses_on_its_own()
        {
            var l = new RttyTunerLeases();
            l.Start("main", At(0));
            l.Start("popout", At(0));
            for (int s = 1; s <= 20; s++) l.Poll("popout", At(s));

            Assert.Null(l.Expire(At(20)));
            Assert.False(l.Holds("main"));
            Assert.True(l.Holds("popout"));
        }

        [Fact]
        public void Every_window_gone_quiet_stops_as_idle()
        {
            var l = new RttyTunerLeases();
            l.Start("a", At(0));
            l.Start("b", At(0));

            Assert.Null(l.Expire(At(15)));
            Assert.Equal("no page polling", l.Expire(At(15.1)));
        }

        [Fact]
        public void A_poll_does_not_create_a_lease()
        {
            // A request still in flight when its window closed must not bring
            // the hold back once the stop has taken effect.
            var l = new RttyTunerLeases();
            l.Start("main", At(0));
            l.Stop("main", At(0));
            Assert.Equal("dialog closed", l.Expire(At(2)));

            Assert.False(l.Poll("main", At(2.1)));
            Assert.Equal(0, l.Count);
        }

        [Fact]
        public void A_poll_does_not_cancel_a_stop()
        {
            var l = new RttyTunerLeases();
            l.Start("main", At(0));
            l.Stop("main", At(0));
            Assert.True(l.Poll("main", At(1)));
            Assert.Equal("dialog closed", l.Expire(At(2)));
        }

        [Fact]
        public void A_page_with_no_id_shares_one_lease()
        {
            var l = new RttyTunerLeases();
            l.Start(null, At(0));
            Assert.True(l.Poll(null, At(1)));
            Assert.True(l.Holds(""));
            l.Stop(null, At(1));
            Assert.Equal("dialog closed", l.Expire(At(3)));
        }

        [Fact]
        public void Expire_with_no_leases_says_nothing()
        {
            Assert.Null(new RttyTunerLeases().Expire(At(100)));
        }
    }
}
