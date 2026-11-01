using RadioWebControl.Core.Services.Cw;
using Xunit;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// CwKeyingPlayer plays a browser keyer's events on the shack PC. What the
    /// operator hears on air depends on two things these tests pin: element
    /// lengths come out exactly as keyed whatever the network does within the
    /// buffer, and the key is let go whenever something is wrong - silence,
    /// a key held too long, lost events, an abort.
    ///
    /// The network is simulated: a sender keys elements (sender clock), sends
    /// a packet for each element carrying it and the events before it, and a
    /// ping every 100 ms; each packet arrives 4000 ms + its delay later on the
    /// shack's clock (the two clocks have unrelated bases).
    /// </summary>
    public class CwKeyingPlayerTests
    {
        private const double ClockBase = 4000;

        private sealed class Line : ICwKeyLine
        {
            public double Now;
            public readonly List<(bool Down, double At)> Changes = new();
            public void SetKey(bool down) => Changes.Add((down, Now));
        }

        private sealed class Sim
        {
            public readonly Line Line = new();
            public readonly CwKeyingPlayer Player;
            public readonly List<CwKeyingReport> Reports = new();
            private readonly List<(double Arrive, CwKeyPacket Packet)> _net = new();
            private readonly List<CwKeyEvent> _sent = new();
            private long _seq;

            public Sim(CwKeyingOptions? o = null)
            {
                Player = new CwKeyingPlayer(Line, o);
                Player.Report += Reports.Add;
            }

            /// <summary>Keys an element: sends its down and up in one packet at <paramref name="start"/>.</summary>
            public void Element(double start, double end, double delay, bool deliver = true)
            {
                _sent.Add(new CwKeyEvent { Seq = _seq++, Down = true, T = start });
                _sent.Add(new CwKeyEvent { Seq = _seq++, Down = false, T = end });
                if (deliver) Send(start, delay, _sent.TakeLast(8).ToList());
            }

            public void Down(double t, double delay)
            {
                _sent.Add(new CwKeyEvent { Seq = _seq++, Down = true, T = t });
                Send(t, delay, _sent.TakeLast(8).ToList());
            }

            public void Pings(double from, double to, double delay)
            {
                for (double t = from; t <= to; t += 100) Send(t, delay, new List<CwKeyEvent>());
            }

            public void Send(double senderNow, double delay, List<CwKeyEvent> events, bool abort = false) =>
                _net.Add((senderNow + ClockBase + delay, new CwKeyPacket { Now = senderNow, Events = events, Abort = abort }));

            public void Run(double until = 1e9)
            {
                var pending = _net.OrderBy(x => x.Arrive).ToList();
                int i = 0;
                for (;;)
                {
                    double? due = Player.NextDueMs;
                    double next = Math.Min(i < pending.Count ? pending[i].Arrive : double.MaxValue, due ?? double.MaxValue);
                    if (next == double.MaxValue || next > until) break;
                    Line.Now = next;
                    if (i < pending.Count && pending[i].Arrive == next) Player.Receive(pending[i++].Packet, next);
                    else Player.Tick(next);
                }
                // Delivered packets are gone; a later Run must not send them again.
                _net.Clear();
                _net.AddRange(pending.Skip(i));
            }

            /// <summary>(down at, up at) pairs, in shack time.</summary>
            public List<(double Down, double Up)> Elements()
            {
                var list = new List<(double, double)>();
                for (int k = 0; k + 1 < Line.Changes.Count; k += 2)
                {
                    Assert.True(Line.Changes[k].Down);
                    Assert.False(Line.Changes[k + 1].Down);
                    list.Add((Line.Changes[k].At, Line.Changes[k + 1].At));
                }
                return list;
            }
        }

        // PARIS-ish run of elements at 20 WPM (60 ms unit), starting at 1000.
        private static List<(double Start, double End)> Keyed(int count = 12)
        {
            var list = new List<(double, double)>();
            double t = 1000;
            for (int k = 0; k < count; k++)
            {
                double len = k % 3 == 1 ? 180 : 60;
                list.Add((t, t + len));
                t += len + (k % 4 == 3 ? 180 : 60);
            }
            return list;
        }

        [Fact]
        public void Steady_network_plays_every_element_after_the_buffer_with_exact_timing()
        {
            var sim = new Sim();
            foreach (var (s, e) in Keyed()) sim.Element(s, e, delay: 40);
            sim.Run();
            var played = sim.Elements();
            var keyed = Keyed();
            Assert.Equal(keyed.Count, played.Count);
            for (int k = 0; k < keyed.Count; k++)
            {
                Assert.Equal(keyed[k].Start + ClockBase + 40 + 150, played[k].Down, 6);
                Assert.Equal(keyed[k].End + ClockBase + 40 + 150, played[k].Up, 6);
            }
        }

        [Fact]
        public void Jitter_inside_the_buffer_changes_nothing()
        {
            var sim = new Sim();
            var rnd = new Random(7);
            var keyed = Keyed(40);
            foreach (var (s, e) in keyed) sim.Element(s, e, delay: 20 + rnd.NextDouble() * 120);
            sim.Run();
            var played = sim.Elements();
            Assert.Equal(keyed.Count, played.Count);
            // Every gap and every element as keyed, to a microsecond.
            for (int k = 0; k < keyed.Count; k++)
            {
                Assert.Equal(keyed[k].End - keyed[k].Start, played[k].Up - played[k].Down, 3);
                if (k > 0) Assert.Equal(keyed[k].Start - keyed[k - 1].End, played[k].Down - played[k - 1].Up, 3);
            }
            Assert.DoesNotContain(sim.Reports, r => r.Kind == "safety");
        }

        [Theory]
        [InlineData(5)]
        [InlineData(12)]
        [InlineData(25)]
        [InlineData(35)]
        [InlineData(50)]
        public void Every_speed_plays_exactly_through_jitter(int wpm)
        {
            // Random letters at this speed: dits, dahs, element, letter and
            // word spaces, every packet delayed by 20-140 ms.
            double u = 1200.0 / wpm, t = 1000;
            var rnd = new Random(wpm);
            var keyed = new List<(double Start, double End)>();
            for (int k = 0; k < 60; k++)
            {
                double len = rnd.Next(2) == 0 ? u : 3 * u;
                keyed.Add((t, t + len));
                t += len + (k % 7 == 6 ? 7 * u : k % 3 == 2 ? 3 * u : u);
            }
            var sim = new Sim();
            foreach (var (s, e) in keyed) sim.Element(s, e, delay: 20 + rnd.NextDouble() * 120);
            // The page pings every 100 ms whatever the key is doing: without
            // them a 720 ms dah at 5 WPM looks like a lost link.
            sim.Pings(900, t + 1000, delay: 40);
            sim.Run();
            var played = sim.Elements();
            Assert.Equal(keyed.Count, played.Count);
            for (int k = 0; k < keyed.Count; k++)
            {
                Assert.Equal(keyed[k].End - keyed[k].Start, played[k].Up - played[k].Down, 3);
                if (k == 0) continue;
                double gap = keyed[k].Start - keyed[k - 1].End, playedGap = played[k].Down - played[k - 1].Up;
                // Short gaps exactly; a long one within 10%, following the offset.
                if (gap <= 500) Assert.Equal(gap, playedGap, 3);
                else Assert.InRange(playedGap, gap * 0.9 - 1e-6, gap * 1.1 + 1e-6);
            }
            Assert.DoesNotContain(sim.Reports, r => r.Kind == "safety");
        }

        [Fact]
        public void A_long_gap_catches_up_gradually()
        {
            // A slow first packet sets the timeline late; a faster one later
            // moves the offset 100 ms earlier. Each word space gives up at
            // most 70 ms of its 700, so it takes two to catch up.
            var sim = new Sim();
            sim.Element(1000, 1100, delay: 140);
            sim.Element(1800, 1900, delay: 40);
            sim.Element(2600, 2700, delay: 40);
            sim.Element(3400, 3500, delay: 40);
            sim.Pings(900, 4500, delay: 140);
            sim.Run();
            var p = sim.Elements();
            Assert.Equal(4, p.Count);
            Assert.Equal(630, p[1].Down - p[0].Up, 3);
            Assert.Equal(670, p[2].Down - p[1].Up, 3);
            Assert.Equal(700, p[3].Down - p[2].Up, 3);
            Assert.All(p, e => Assert.Equal(100, e.Up - e.Down, 3));
        }

        [Fact]
        public void A_packet_later_than_the_buffer_plays_late_but_at_full_length()
        {
            var sim = new Sim();
            sim.Element(1000, 1060, delay: 40);
            sim.Element(1120, 1300, delay: 450);   // 410 ms slower than the first: past the 150 ms buffer
            sim.Run();
            var played = sim.Elements();
            Assert.Equal(2, played.Count);
            Assert.Equal(180, played[1].Up - played[1].Down, 6);
            Assert.True(played[1].Down > 1120 + ClockBase + 40 + 150, "it could not play on time");
            Assert.Contains(sim.Reports, r => r.Kind == "key" && r.LateMs > 100);
        }

        [Fact]
        public void A_lost_packet_is_covered_by_the_repeats_in_the_next()
        {
            var sim = new Sim();
            var keyed = Keyed();
            for (int k = 0; k < keyed.Count; k++) sim.Element(keyed[k].Start, keyed[k].End, delay: 40, deliver: k != 5);
            sim.Run();
            Assert.Equal(keyed.Count, sim.Elements().Count);
            Assert.DoesNotContain(sim.Reports, r => r.Kind == "safety");
        }

        [Fact]
        public void Events_lost_beyond_the_repeats_let_go_and_wait_for_the_next_key_down()
        {
            var sim = new Sim();
            var keyed = Keyed(12);
            for (int k = 0; k < keyed.Count; k++)
                sim.Element(keyed[k].Start, keyed[k].End, delay: 40, deliver: k < 3 || k > 7);   // 5 packets (10 events) lost
            sim.Run();
            Assert.Contains(sim.Reports, r => r.Kind == "safety" && r.Text.Contains("lost"));
            Assert.False(sim.Player.KeyDown);
            // The first 3 elements, then from the first element after the gap
            // that can still play on time; the stale repeats are skipped.
            var played = sim.Elements();
            Assert.Equal(3 + 4, played.Count);
            for (int k = 3; k < played.Count; k++)
                Assert.Equal(keyed[k + 5].End - keyed[k + 5].Start, played[k].Up - played[k].Down, 6);
        }

        [Fact]
        public void Silence_with_the_key_down_lets_go_after_LinkLostMs()
        {
            var sim = new Sim();
            sim.Down(1000, delay: 40);     // the up never arrives, nor anything else
            sim.Run();
            Assert.Equal(new[] { (true, 1000 + ClockBase + 40 + 150), (false, 1000 + ClockBase + 40 + 800) },
                         sim.Line.Changes.ToArray());
            Assert.Contains(sim.Reports, r => r.Kind == "safety" && r.Text.Contains("nothing from the browser"));
        }

        [Fact]
        public void A_key_held_too_long_is_let_go_even_with_the_link_alive()
        {
            var sim = new Sim();
            sim.Down(1000, delay: 40);
            sim.Pings(1100, 6000, delay: 40);
            sim.Run();
            double down = 1000 + ClockBase + 40 + 150;
            Assert.Equal(new[] { (true, down), (false, down + 3000) }, sim.Line.Changes.ToArray());
            Assert.Contains(sim.Reports, r => r.Kind == "safety" && r.Text.Contains("key down for"));
        }

        [Fact]
        public void Abort_with_the_key_down_lets_go_at_once()
        {
            var sim = new Sim();
            sim.Down(1000, delay: 40);                                 // plays at 5190
            sim.Send(1200, 40, new List<CwKeyEvent>(), abort: true);   // arrives at 5240
            sim.Element(1300, 1360, delay: 40);                       // a new down after the abort plays again
            sim.Run();
            Assert.Equal(new[] { (true, 5190.0), (false, 5240.0), (true, 5490.0), (false, 5550.0) },
                         sim.Line.Changes.ToArray());
        }

        [Fact]
        public void Abort_drops_what_is_queued_and_not_yet_played()
        {
            var sim = new Sim();
            sim.Element(1000, 1180, delay: 40);                       // due at 5190
            sim.Send(1100, 40, new List<CwKeyEvent>(), abort: true);   // arrives at 5140, first
            sim.Run();
            Assert.Empty(sim.Line.Changes);
            Assert.Contains(sim.Reports, r => r.Kind == "safety" && r.Text.StartsWith("Stopped"));
        }

        [Fact]
        public void After_Reset_a_new_sender_numbering_from_zero_on_its_own_clock_plays()
        {
            var sim = new Sim();
            foreach (var (s, e) in Keyed(20)) sim.Element(s, e, delay: 40);
            sim.Run();
            Assert.Equal(20, sim.Elements().Count);

            // A second browser: sequence from 0 again, and a clock 50 s behind.
            double now = sim.Line.Now + 1000;
            sim.Player.Reset(now);
            sim.Line.Now = now + 10;
            sim.Player.Receive(new CwKeyPacket
            {
                Now = 200,
                Events = { new CwKeyEvent { Seq = 0, Down = true, T = 205 }, new CwKeyEvent { Seq = 1, Down = false, T = 265 } },
            }, now + 10);
            sim.Run();
            var p = sim.Elements();
            Assert.Equal(21, p.Count);
            Assert.Equal(now + 10 + 5 + 150, p[20].Down, 6);
            Assert.Equal(60, p[20].Up - p[20].Down, 6);
        }

        [Fact]
        public void A_new_player_does_not_play_repeats_of_events_sent_before_it()
        {
            // YWC restarted while the page stayed open: the first ping it hears
            // repeats elements keyed minutes ago. Seen 2026-10-31, played 637 s late.
            var sim = new Sim();
            var old = new List<CwKeyEvent>();
            for (int k = 0; k < 4; k++)
            {
                old.Add(new CwKeyEvent { Seq = 220 + 2 * k, Down = true, T = 1000 + 240 * k });
                old.Add(new CwKeyEvent { Seq = 221 + 2 * k, Down = false, T = 1180 + 240 * k });
            }
            sim.Send(600_000, 40, old);
            sim.Pings(600_100, 601_000, 40);
            sim.Run();
            Assert.Empty(sim.Line.Changes);

            // The next element keyed plays as usual.
            sim.Send(601_050, 40, old.Append(new CwKeyEvent { Seq = 228, Down = true, T = 601_050 })
                                     .Append(new CwKeyEvent { Seq = 229, Down = false, T = 601_110 }).TakeLast(8).ToList());
            sim.Pings(601_150, 602_000, 40);
            sim.Run();
            var p = sim.Elements();
            Assert.Single(p);
            Assert.Equal(601_050 + ClockBase + 40 + 150, p[0].Down, 6);
            Assert.Equal(60, p[0].Up - p[0].Down, 6);
        }

        [Fact]
        public void A_faster_packet_later_does_not_squeeze_a_character()
        {
            // The first packets are slow; a fast one arrives mid-character and
            // lowers the offset. The character keeps its spacing; the timeline
            // catches up at the next long gap, by at most 10% of it: 70 of
            // the 100 ms it is behind.
            var sim = new Sim();
            sim.Element(1000, 1060, delay: 120);
            sim.Element(1120, 1180, delay: 20);
            sim.Element(1240, 1300, delay: 20);
            sim.Element(2000, 2060, delay: 20);
            sim.Run();
            var p = sim.Elements();
            Assert.Equal(60, p[1].Down - p[0].Up, 6);
            Assert.Equal(60, p[2].Down - p[1].Up, 6);
            Assert.Equal(630, p[3].Down - p[2].Up, 6);
            Assert.Equal(2000 + ClockBase + 20 + 150 + 30, p[3].Down, 6);
        }
    }
}
