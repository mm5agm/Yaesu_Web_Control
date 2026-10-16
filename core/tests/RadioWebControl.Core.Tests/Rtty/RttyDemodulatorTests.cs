using System;
using System.Linq;
using RadioWebControl.Core.Services.Rtty;
using Xunit;

namespace RadioWebControl.Core.Tests.Rtty
{
    /// <summary>
    /// The decoder, against signals whose content is known exactly.
    ///
    /// This is the whole reason the modulator was written first. Against the air
    /// there is no way to tell a decoder fault from a fading signal, and no way
    /// to run the same signal twice; here the text that went in is known, so a
    /// wrong character is the decoder and nothing else.
    /// </summary>
    public class RttyDemodulatorTests
    {
        private const int Rate = 48_000;

        /// <summary>Text to audio and back, with no noise in between.</summary>
        private static string RoundTrip(
            string text,
            double markHz = 2125,
            int shiftHz = 170,
            double baud = RttyModulator.Baud45,
            RttyFigureSet set = RttyFigureSet.Ita2,
            bool usos = true,
            double stopBits = 1.5,
            int sampleRate = Rate,
            double noiseSigma = 0,
            int seed = 1,
            bool reverseAudio = false,
            RttyDemodulator? into = null)
        {
            var spaceHz = markHz + shiftHz;

            var audio = RttyModulator.ToAudio(
                text, sampleRate,
                reverseAudio ? spaceHz : markHz,
                reverseAudio ? markHz : spaceHz,
                baud, set, usos, stopBits);

            if (noiseSigma > 0) AddNoise(audio, noiseSigma, seed);

            var rx = into ?? new RttyDemodulator(sampleRate, markHz, spaceHz, baud, set, usos);
            return rx.Feed(audio);
        }

        /// <summary>
        /// Gaussian noise, from a fixed seed so a failure can be looked at
        /// rather than wondered about.
        /// </summary>
        private static void AddNoise(float[] audio, double sigma, int seed)
        {
            var rng = new Random(seed);
            for (int i = 0; i < audio.Length; i++)
            {
                // Box-Muller.
                var u1 = 1.0 - rng.NextDouble();
                var u2 = rng.NextDouble();
                var n = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
                audio[i] = (float)(audio[i] + sigma * n);
            }
        }

        /// <summary>Three seconds of nothing but noise, at a repeatable seed.</summary>
        private static float[] Hiss(int seconds, double sigma = 1.0, int seed = 7)
        {
            var a = new float[Rate * seconds];
            AddNoise(a, sigma, seed);
            return a;
        }

        // --- a signal straight through ---------------------------------------

        [Fact]
        public void A_clean_signal_comes_back_exactly()
        {
            Assert.Equal("CQ CQ DE MM5AGM MM5AGM K", RoundTrip("CQ CQ DE MM5AGM MM5AGM K"));
        }

        [Fact]
        public void Figures_come_back_too()
        {
            Assert.Equal("RST 599 QTH IS 55N 4W", RoundTrip("RST 599 QTH IS 55N 4W"));
        }

        [Theory]
        [InlineData(RttyFigureSet.Ita2)]
        [InlineData(RttyFigureSet.UsTty)]
        public void Both_figure_sets_survive_the_signal_path(RttyFigureSet set)
        {
            const string text = "WX 1013 MB 12 KT 350 DEG";
            Assert.Equal(text, RoundTrip(text, set: set));
        }

        [Fact]
        public void A_long_transmission_does_not_drift()
        {
            // The timing reference is each character's own start edge, so an
            // error cannot accumulate across a transmission - but the claim is
            // worth testing rather than asserting, because the fractional bit
            // period is exactly the sort of thing that would break it.
            // 40 characters at 45.45 baud is six and a half seconds of audio.
            Assert.Equal(Pangram, RoundTrip(Pangram));
        }

        [Fact]
        public void A_clean_signal_produces_no_framing_errors()
        {
            var rx = new RttyDemodulator(Rate, 2125, 2295, RttyModulator.Baud45);
            RoundTrip("DE MM5AGM", into: rx);

            Assert.Equal(0, rx.FramingErrors);
            Assert.True(rx.Characters > 0);
        }

        // --- the parameters a listener actually meets -------------------------

        [Theory]
        [InlineData(170, RttyModulator.Baud45)]   // amateur
        [InlineData(425, RttyModulator.Baud50)]   // DDK9 and the other weather stations
        [InlineData(450, RttyModulator.Baud50)]   // what DWD publishes
        [InlineData(850, 75.0)]                   // military and aeronautical circuits
        [InlineData(170, 75.0)]
        [InlineData(170, 100.0)]
        public void Every_shift_and_speed_a_listener_meets_decodes(int shift, double baud)
        {
            const string text = "DDK9 DEUTSCHER WETTERDIENST";
            Assert.Equal(text, RoundTrip(text, shiftHz: shift, baud: baud));
        }

        [Fact]
        public void A_station_sending_reversed_is_handled_by_swapping_the_two_tones()
        {
            // No Reverse flag on the demodulator on purpose: there is nothing for
            // one to do that passing the frequencies the other way round does not
            // already do, and a flag as well as an order is two ways to say the
            // same thing, which is how a tuner ends up double-reversing itself.
            const string text = "REVERSED";

            var spaceBelow = RttyModulator.ToAudio(text, Rate, 2125, 2125 - 170, RttyModulator.Baud45);

            var right = new RttyDemodulator(Rate, 2125, 2125 - 170, RttyModulator.Baud45);
            Assert.Equal(text, right.Feed(spaceBelow));

            // And the wrong way round does not accidentally work.
            var wrong = new RttyDemodulator(Rate, 2125 - 170, 2125, RttyModulator.Baud45);
            Assert.NotEqual(text, wrong.Feed(spaceBelow));
        }

        [Theory]
        [InlineData(1.0)]
        [InlineData(1.5)]
        [InlineData(2.0)]
        public void Any_of_the_three_stop_lengths_in_use_decodes(double stopBits)
        {
            // The receiver is never told which: it resynchronises on every start
            // edge and only needs the stop element to be at least one bit long.
            Assert.Equal("STOP BITS", RoundTrip("STOP BITS", stopBits: stopBits));
        }

        [Theory]
        [InlineData(48_000)]
        [InlineData(44_100)]
        [InlineData(11_025)]
        [InlineData(8_000)]
        public void It_works_at_any_sample_rate_a_sound_card_offers(int rate)
        {
            Assert.Equal("SAMPLE RATE", RoundTrip("SAMPLE RATE", sampleRate: rate));
        }

        // --- noise -----------------------------------------------------------

        [Fact]
        public void A_signal_at_the_strength_a_listener_would_call_good_is_copied_perfectly()
        {
            // The noise figure is chosen to mean something rather than to pass.
            // Signal power is A^2/2 = 0.125 at the default amplitude. White noise
            // of per-sample variance s^2 spreads over rate/2 = 24 kHz, so the
            // part of it inside a 500 Hz receiver filter - the width an operator
            // would use for RTTY - is s^2 * 500/24000. Setting s = 0.8 puts that
            // at about -8 dB relative to the signal, which is a comfortable but
            // not strong signal.
            const string text = "CQ CQ DE MM5AGM K";
            Assert.Equal(text, RoundTrip(text, noiseSigma: 0.8));
        }

        [Fact]
        public void A_weak_signal_is_still_copied_perfectly()
        {
            // About 2 dB signal-to-noise in a 500 Hz bandwidth, which on the air
            // is a station you would have to concentrate to copy by ear. This is
            // the test that pays for separating the printing gate from the bit
            // decision: with one threshold doing both jobs this level came back
            // 53% right.
            Assert.Equal(Pangram, RoundTrip(Pangram, noiseSigma: 2.0));
        }

        [Fact]
        public void Signal_activity_tells_a_station_from_an_empty_band()
        {
            // The number the printing gate is compared against, and the honest
            // answer to "is anything there?" - so worth testing in its own right
            // rather than only through its effect. Noise sits near a third
            // because two independent noise magnitudes are usually of similar
            // size; a station sits high, but not at one - measured at 0.80 on a
            // noiseless signal, because the averaging window is a whole bit long
            // and so spans every tone transition, where neither tone is winning.
            // RTTY transitions constantly, so that is a permanent cost and not a
            // fault. It is also the reason the gate sits at 0.45 rather than
            // somewhere nearer 1.
            var quiet = new RttyDemodulator(Rate, 2125, 2295, RttyModulator.Baud45);
            quiet.Feed(Hiss(seconds: 3));

            var busy = new RttyDemodulator(Rate, 2125, 2295, RttyModulator.Baud45);
            RoundTrip(Pangram, into: busy);

            Assert.InRange(quiet.SignalActivity, 0.1, 0.45);
            Assert.True(busy.SignalActivity > 0.7, $"a clean signal read {busy.SignalActivity:F2}");
        }

        [Fact]
        public void The_gate_opens_quickly_enough_not_to_eat_the_first_word()
        {
            // It once did, at 75 and 100 baud, because the averaging time was
            // half a second however fast the signal was. Twenty bit times of idle
            // is all a transmission need offer.
            foreach (var baud in new[] { RttyModulator.Baud45, RttyModulator.Baud50, 75.0, 100.0 })
                Assert.Equal("FIRST WORD", RoundTrip("FIRST WORD", baud: baud));
        }

        [Fact]
        public void A_signal_at_about_the_noise_floor_is_still_mostly_readable()
        {
            // s = 2.45 by the arithmetic above is roughly 0 dB signal-to-noise in
            // 500 Hz, which is about where RTTY stops being copyable at all. The
            // test is not that it is perfect - it is that the decoder degrades by
            // corrupting characters rather than by collapsing. Measured at 74%:
            // "HEPUICK BROWM FOXIMHSIOVER THE LZ DOG", which an operator would
            // read without much trouble.
            var got = RoundTrip(Pangram, noiseSigma: 2.45);

            var score = Agreement(Pangram, got);
            Assert.True(score > 0.6, $"copied {score:P0} at the noise floor: \"{got}\"");
        }

        [Fact]
        public void Noise_on_its_own_does_not_print_pages_of_letters()
        {
            // What the squelch is for, and the test that set its default. An idle
            // receiver that believed every zero crossing fills the screen, and on
            // a quiet band that is all the operator would ever see.
            //
            // Three seconds is eighteen characters of real RTTY, so eighteen is
            // the ceiling and the first version of this decoder hit thirteen of
            // them on pure hiss.
            var rx = new RttyDemodulator(Rate, 2125, 2295, RttyModulator.Baud45);
            var printed = rx.Feed(Hiss(seconds: 3));

            Assert.True(printed.Length <= 5, $"noise printed \"{printed}\"");
        }

        [Fact]
        public void The_quiet_screen_is_the_squelch_and_not_a_deaf_decoder()
        {
            // The other half of the previous test, and the reason it is worth
            // writing: a decoder whose filters simply never responded to noise
            // would pass that test too, and would be throwing away weak signals
            // to do it. Opening the squelch has to let the junk back in.
            var hiss = Hiss(seconds: 3);

            var open = new RttyDemodulator(Rate, 2125, 2295, RttyModulator.Baud45) { Squelch = 0 };
            var shut = new RttyDemodulator(Rate, 2125, 2295, RttyModulator.Baud45);

            var openCount = open.Feed(hiss).Length;
            var shutCount = shut.Feed(hiss).Length;

            // Measured: 13 wide open against 3 at the default - against a
            // theoretical ceiling of 18, so wide open is close to saturated.
            Assert.True(openCount >= 10, $"wide open printed only {openCount}");
            Assert.True(openCount > shutCount * 2, $"open {openCount} vs default {shutCount}");
        }

        // --- the diagnostics claim their own behaviour ------------------------

        private const string Pangram = "THE QUICK BROWN FOX JUMPS OVER THE LAZY DOG";

        [Fact]
        public void A_grossly_wrong_speed_breaks_the_framing()
        {
            var audio = RttyModulator.ToAudio(Pangram, Rate, 2125, 2295, RttyModulator.Baud45);

            var right = new RttyDemodulator(Rate, 2125, 2295, RttyModulator.Baud45);
            right.Feed(audio);
            Assert.Equal(0, right.FramingErrors);

            var wrong = new RttyDemodulator(Rate, 2125, 2295, 100.0);
            wrong.Feed(audio);
            Assert.True(wrong.FramingErrors > 0, "45 baud read at 100 should not frame cleanly");
        }

        [Fact]
        public void A_slightly_wrong_speed_corrupts_the_text_while_the_framing_stays_clean()
        {
            // Worth pinning down, because the obvious reading of FramingErrors is
            // that it tells you whether the settings are right, and here it does
            // not. A 50 baud station decoded at 45.45 puts the stop sample about a
            // third of a bit late, which a one-and-a-half-bit stop element
            // absorbs completely - so the framing looks perfect while the data
            // bits are read at the wrong instants. The text is the witness, not
            // the counter.
            var audio = RttyModulator.ToAudio(Pangram, Rate, 2125, 2295, RttyModulator.Baud50);

            var wrong = new RttyDemodulator(Rate, 2125, 2295, RttyModulator.Baud45);
            var got = wrong.Feed(audio);

            Assert.NotEqual(Pangram, got);
            Assert.Equal(0, wrong.FramingErrors);
        }

        [Fact]
        public void Reset_forgets_the_shift_state_and_the_part_decoded_character()
        {
            var rx = new RttyDemodulator(Rate, 2125, 2295, RttyModulator.Baud45);

            RoundTrip("599", into: rx);
            Assert.True(rx.InFigures);

            rx.Reset();
            Assert.False(rx.InFigures);

            // And it can be used again afterwards.
            Assert.Equal("ABC", RoundTrip("ABC", into: rx));
        }

        [Fact]
        public void Audio_can_arrive_in_frames_of_any_size()
        {
            // How it will actually be fed: a sound card hands over a few hundred
            // samples at a time, and a character spans many frames.
            const string text = "FRAMED AUDIO 123";
            var audio = RttyModulator.ToAudio(text, Rate, 2125, 2295, RttyModulator.Baud45);

            var rx = new RttyDemodulator(Rate, 2125, 2295, RttyModulator.Baud45);
            var got = string.Empty;
            for (int at = 0; at < audio.Length; at += 480)
                got += rx.Feed(audio.AsSpan(at, Math.Min(480, audio.Length - at)));

            Assert.Equal(text, got);
        }

        /// <summary>
        /// Roughly how much of the expected text came through, as a fraction.
        /// A longest-common-subsequence ratio, so a dropped character costs one
        /// character rather than throwing everything after it out of step.
        /// </summary>
        private static double Agreement(string expected, string got)
        {
            var table = new int[expected.Length + 1, got.Length + 1];
            for (int i = 1; i <= expected.Length; i++)
                for (int j = 1; j <= got.Length; j++)
                    table[i, j] = expected[i - 1] == got[j - 1]
                        ? table[i - 1, j - 1] + 1
                        : Math.Max(table[i - 1, j], table[i, j - 1]);

            return (double)table[expected.Length, got.Length] / expected.Length;
        }
    }
}
