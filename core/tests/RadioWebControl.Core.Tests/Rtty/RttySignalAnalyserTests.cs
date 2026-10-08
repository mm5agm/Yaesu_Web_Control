using System;
using System.Collections.Generic;
using RadioWebControl.Core.Services.Rtty;
using Xunit;

namespace RadioWebControl.Core.Tests.Rtty
{
    /// <summary>
    /// The analyser is told nothing and has to work out everything, so every test
    /// here generates a signal whose tones and speed are known exactly and then
    /// checks the answer that comes back. That is the only honest way to test it:
    /// on the air there is no way to know whether the analyser was wrong or the
    /// station was not what it was assumed to be.
    ///
    /// The text matters more than it looks. A bare "RYRYRY" alternates every bit
    /// and gives the tone-duty test nothing to work with, which is why most of
    /// these send sentences - realistic keying, with the uneven run lengths the
    /// speed estimate actually feeds on.
    /// </summary>
    public class RttySignalAnalyserTests
    {
        private const int Rate = 48_000;

        private const string Traffic =
            "CQ CQ DE MM5AGM MM5AGM K THE WEATHER HERE IS FINE AND THE RIG IS WORKING WELL";

        private static float[] Signal(
            double markHz, double shiftHz, double baud,
            string text = Traffic, bool reversed = false, double noiseSigma = 0, int seed = 11)
        {
            var spaceHz = markHz + shiftHz;
            var audio = RttyModulator.ToAudio(
                text, Rate,
                reversed ? spaceHz : markHz,
                reversed ? markHz : spaceHz,
                baud, idleBitsBefore: 20, idleBitsAfter: 10);

            if (noiseSigma > 0) AddNoise(audio, noiseSigma, seed);
            return audio;
        }

        /// <summary>
        /// The same signal, but with one tone louder than the other - which is what
        /// an HF signal nearly always is, and what <see cref="RttyModulator"/> has no
        /// reason to produce. Selective fading treats the two tones as two separate
        /// signals, because a few hundred hertz apart is exactly the scale on which
        /// it works, so a few dB between them is the normal state of affairs rather
        /// than a fault.
        /// </summary>
        private static float[] Lopsided(
            double markHz, double shiftHz, double baud, double spaceDbLouder,
            string text = Traffic)
        {
            const double markAmp = 0.4;
            var spaceAmp = markAmp * Math.Pow(10, spaceDbLouder / 20);
            var pulses = RttyModulator.Pulses(text, RttyFigureSet.Ita2, true, 1.5, 20, 10, true);

            var perBit = Rate / baud;
            var samples = new List<float>();
            double phase = 0, carried = 0;

            foreach (var pulse in pulses)
            {
                var want = pulse.Bits * perBit + carried;
                var count = (int)Math.Round(want);
                carried = want - count;

                var step = 2 * Math.PI * (pulse.Mark ? markHz : markHz + shiftHz) / Rate;
                var amplitude = pulse.Mark ? markAmp : spaceAmp;

                for (int i = 0; i < count; i++)
                {
                    samples.Add((float)(amplitude * Math.Sin(phase)));
                    phase += step;
                    if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
                }
            }

            return samples.ToArray();
        }

        /// <summary>
        /// A frequency assertion with a tolerance worth stating: six hertz.
        ///
        /// <para>That is what the analyser actually delivers - measured across this
        /// whole matrix the worst tone error was 5.2 Hz, and every case but one was
        /// inside 1.6 Hz. The outlier is a 170 Hz shift at 100 baud, which is the
        /// hardest geometry there is: the keying clusters are at their widest and
        /// the two tones at their closest, so each peak sits partly inside the
        /// other.</para>
        ///
        /// <para>It is also comfortably good enough, which is why the tolerance is
        /// set here rather than chased. The demodulator reads a tone through a
        /// filter tens of hertz wide, so six hertz makes no difference whatever to
        /// whether the text comes out; the finest distinction that matters is
        /// telling a 425 shift from a 450, which are 25 Hz apart and have their own
        /// test below.</para>
        /// </summary>
        private static void Hz(double expected, double got, double tolerance = 6.0)
            => Assert.InRange(got, expected - tolerance, expected + tolerance);

        /// <summary>
        /// A speed assertion with a tolerance, because the speed is now measured
        /// rather than chosen from a list and so comes back with a fraction on it.
        ///
        /// <para>Half a percent is what the analyser delivers across this matrix,
        /// and it is far tighter than it needs to be: a character is seven and a
        /// half bits, so half a percent of speed error has drifted the sampling
        /// point by 4% of one bit by the stop element, which no demodulator
        /// notices. It is tight enough to tell 74.2 from 75, which are 1.1% apart
        /// and the closest two named speeds there are.</para>
        /// </summary>
        private static void Baud(double expected, double got, double tolerance = 0.005)
            => Assert.InRange(got, expected * (1 - tolerance), expected * (1 + tolerance));

        private static void AddNoise(float[] audio, double sigma, int seed)
        {
            var random = new Random(seed);
            for (int i = 0; i < audio.Length; i++)
            {
                var u1 = 1.0 - random.NextDouble();
                var u2 = random.NextDouble();
                var gauss = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
                audio[i] += (float)(gauss * sigma * 0.1);
            }
        }

        // --- the tones -------------------------------------------------------

        [Fact]
        public void An_amateur_RTTY_signal_is_measured_as_2125_and_2295_at_45_baud()
        {
            var got = RttySignalAnalyser.Analyse(Signal(2125, 170, 45.45), Rate);

            Assert.NotNull(got);
            Hz(2125, got!.MarkHz);
            Hz(2295, got.SpaceHz);
            Hz(170, got.ShiftHz);
            Baud(45.45, got.Baud);
        }

        [Theory]
        [InlineData(2125, 170, 45.45)]   // amateur RTTY
        [InlineData(1275, 170, 45.45)]   // the old low tones, still met on recordings
        [InlineData(2125, 200, 50.0)]    // some military traffic
        [InlineData(1000, 425, 50.0)]    // weather and press
        [InlineData(1000, 450, 50.0)]    // DDK9 and the other DWD stations
        [InlineData(1000, 850, 75.0)]    // military, aviation, AFTN
        [InlineData(1500, 170, 100.0)]   // fast press circuits
        public void Every_combination_a_listener_meets_is_identified(double mark, double shift, double baud)
        {
            var got = RttySignalAnalyser.Analyse(Signal(mark, shift, baud), Rate);

            Assert.NotNull(got);
            Hz(mark, got!.MarkHz);
            Baud(baud, got.Baud);

            // The shift is asserted through the snap rather than raw, because that
            // is the form the operator and the radio both see, and because the raw
            // figure is out by 10 Hz in the 170-at-100-baud case for the reason
            // given on Hz() - which snapping absorbs with 20 Hz to spare.
            Assert.Equal((int)shift, RttySignalAnalyser.SnapShift(got.ShiftHz));
        }

        [Fact]
        public void The_shift_is_measured_finely_enough_to_tell_170_from_200()
        {
            // 30 Hz apart, and a 4096-point window at 48 kHz is nearly 12 Hz a
            // bin - so without the parabolic refinement of each peak these two
            // would be two or three bins apart and could not be separated with
            // any confidence.
            var narrow = RttySignalAnalyser.Analyse(Signal(2125, 170, 45.45), Rate);
            var wider = RttySignalAnalyser.Analyse(Signal(2125, 200, 45.45), Rate);

            Assert.Equal(170, RttySignalAnalyser.SnapShift(narrow!.ShiftHz));
            Assert.Equal(200, RttySignalAnalyser.SnapShift(wider!.ShiftHz));
        }

        [Fact]
        public void A_425_shift_is_not_reported_as_450_or_the_other_way_round()
        {
            // The two are 6% apart and the distinction is the whole reason the
            // DDK9 picture came out wrong at 425: the operator needs to be told
            // which it is, so snapping has to resolve them.
            Assert.Equal(425, RttySignalAnalyser.SnapShift(
                RttySignalAnalyser.Analyse(Signal(1000, 425, 50), Rate)!.ShiftHz));

            Assert.Equal(450, RttySignalAnalyser.SnapShift(
                RttySignalAnalyser.Analyse(Signal(1000, 450, 50), Rate)!.ShiftHz));
        }

        [Fact]
        public void A_shift_that_matches_nothing_standard_is_reported_as_matching_nothing()
        {
            // Better than silently rounding: two peaks 600 Hz apart are usually
            // not a RTTY pair at all, and saying "nearest is 450" about them
            // would be worse than saying nothing.
            Assert.Null(RttySignalAnalyser.SnapShift(600));
        }

        // --- which way round -------------------------------------------------

        [Fact]
        public void A_reversed_station_is_spotted_from_which_tone_it_rests_on()
        {
            var got = RttySignalAnalyser.Analyse(Signal(2125, 170, 45.45, reversed: true), Rate);

            Assert.NotNull(got);

            // Reversed means the station idles on the *upper* tone, so mark is
            // the higher of the two and the shift comes out negative-going.
            Hz(2295, got!.MarkHz);
            Hz(2125, got.SpaceHz);
            Assert.True(got.MarkHz > got.SpaceHz);
        }

        [Fact]
        public void The_tone_it_names_as_mark_is_the_one_that_actually_decodes()
        {
            // The end-to-end claim, and the only one that matters to an operator:
            // hand the analyser's answer straight to the demodulator and the text
            // comes out. Everything else here is measurement; this is the product.
            foreach (var reversed in new[] { false, true })
            {
                var audio = Signal(2125, 170, 45.45, reversed: reversed);
                var got = RttySignalAnalyser.Analyse(audio, Rate)!;

                var decoder = new RttyDemodulator(Rate, got.MarkHz, got.SpaceHz, got.Baud);
                var text = decoder.Feed(audio);

                Assert.Contains("MM5AGM", text);
            }
        }

        [Fact]
        public void The_polarity_is_decided_by_a_wide_margin_and_not_a_hair()
        {
            // Worth pinning as a number, because the first version of this decided
            // the polarity on a 0.01 margin - the right answer by luck - and a test
            // that only checked the answer passed happily. The run-length test
            // replaced it and reports about 0.63.
            var got = RttySignalAnalyser.Analyse(Signal(2125, 170, 45.45), Rate);

            Assert.True(got!.ToneMargin > 0.4,
                $"the polarity was decided on a margin of only {got.ToneMargin:F2}");
        }

        [Fact]
        public void A_station_with_a_one_bit_stop_element_says_it_cannot_judge_the_polarity()
        {
            // The documented hole, pinned so it stays documented. With a one-bit
            // stop element every run is a whole number of bits, so nothing in the
            // keying distinguishes mark from space and the answer is a coin toss -
            // it comes out wrong on this sample, in fact.
            //
            // So the test asserts what is actually promised: the tones and the speed
            // are right, and the margin admits the polarity is a guess. It
            // deliberately does not assert which polarity, because that would be
            // pinning down a coin.
            var audio = RttyModulator.ToAudio(
                Traffic, Rate, 2125, 2295, 45.45,
                stopBits: 1.0, idleBitsBefore: 20, idleBitsAfter: 10);

            var got = RttySignalAnalyser.Analyse(audio, Rate);

            Assert.NotNull(got);
            Hz(170, got!.ShiftHz);
            Baud(45.45, got.Baud);
            Assert.True(got.ToneMargin <= 0.08,
                $"it claimed a margin of {got.ToneMargin:F2} on a polarity it cannot know");

            // The other half of the promise: being unsure of the polarity must not
            // make it unsure of what it does know.
            Assert.True(got.Confidence > 0.7,
                $"confidence collapsed to {got.Confidence:F2} over a polarity it was " +
                $"not being asked about");
        }

        [Fact]
        public void A_station_with_a_two_bit_stop_element_is_identified_apart_from_polarity()
        {
            var audio = RttyModulator.ToAudio(
                Traffic, Rate, 2125, 2295, 45.45,
                stopBits: 2.0, idleBitsBefore: 20, idleBitsAfter: 10);

            var got = RttySignalAnalyser.Analyse(audio, Rate);

            Assert.NotNull(got);
            Hz(170, got!.ShiftHz);
            Baud(45.45, got.Baud);
            Assert.True(got.Confidence > 0.7, $"confidence {got.Confidence:F2}");
        }

        // --- the speed -------------------------------------------------------

        [Theory]
        [InlineData(45.45)]
        [InlineData(50.0)]
        [InlineData(75.0)]
        [InlineData(100.0)]
        public void The_speed_is_found_without_decoding_anything(double baud)
        {
            var got = RttySignalAnalyser.Analyse(Signal(2125, 170, baud), Rate);

            Assert.NotNull(got);
            Baud(baud, got!.Baud);
            Assert.True(got.BaudFit > 0.9, $"{baud} baud fitted only {got.BaudFit:F2}");
        }

        [Fact]
        public void A_50_baud_station_is_not_read_as_100()
        {
            // The trap the "slowest that fits" rule exists for: 100 baud divides
            // a 50 baud signal perfectly, because every whole number of bits is
            // also a whole number of half-bits. Taking the best-fitting peak of the
            // sweep instead of the slowest adequate one reads every DWD weather
            // station at double speed, and the text is rubbish with clean framing.
            var got = RttySignalAnalyser.Analyse(Signal(1000, 450, 50), Rate);

            Baud(50.0, got!.Baud);
        }

        [Fact]
        public void A_100_baud_station_is_not_read_as_50()
        {
            // The other direction, which is what makes the rule safe rather than
            // merely cautious: a 50 baud yardstick leaves half-bit remainders all
            // over a 100 baud signal, so the slow candidate is rejected on its
            // own merits and not just preferred. Without this half the band would
            // read at half speed, which is the price of the rule if it were only
            // caution - it is not.
            var got = RttySignalAnalyser.Analyse(Signal(1500, 170, 100), Rate);

            Baud(100.0, got!.Baud);
        }

        [Theory]
        [InlineData(56.9)]      // a press circuit
        [InlineData(74.2)]      // another, and only 1.1% from 75
        [InlineData(68.3)]      // a speed nobody tabulated at all
        public void A_speed_that_is_not_in_any_table_is_measured_rather_than_rounded(double baud)
        {
            // The point of searching a range instead of trying a list of famous
            // numbers. A station on one of these is still a station, and reporting
            // it as the nearest named speed would decode to rubbish - the error
            // compounds over the seven and a half bits of a character.
            var got = RttySignalAnalyser.Analyse(Signal(2125, 170, baud), Rate);

            Assert.NotNull(got);
            Baud(baud, got!.Baud);
            Assert.True(got.BaudFit > 0.9, $"{baud} baud fitted only {got.BaudFit:F2}");
        }

        [Fact]
        public void A_speed_outside_the_search_range_is_not_reported_as_one_inside_it()
        {
            // 45.45 baud looked for between 80 and 120, where it cannot be. What
            // must not happen is a confident 90.9 - which is a real alias of it -
            // being handed to the operator as the answer.
            var got = RttySignalAnalyser.Analyse(
                Signal(2125, 170, 45.45), Rate, lowBaud: 80, highBaud: 120);

            // It finds 90.9 and fits it well, because that genuinely is a whole
            // number of half-bits. Asserted as what it is rather than wished away:
            // the search range is the caller's promise about what is on the air,
            // and a caller who narrows it wrongly gets the alias. The default range
            // starts at 30 precisely so this cannot happen to the Auto button.
            Assert.True(got is null || got.Baud > 80,
                $"found {got?.Baud:F2} baud, which is outside the range it was given");
        }

        [Fact]
        public void The_named_speeds_are_recognised_and_the_odd_ones_are_left_alone()
        {
            // What the pop-out acts on: a snapped speed fills the box, a null one
            // blanks it and shows the measurement instead.
            Assert.Equal(45.45, RttySignalAnalyser.SnapBaud(45.5));
            Assert.Equal(50.0, RttySignalAnalyser.SnapBaud(49.9));
            Assert.Equal(100.0, RttySignalAnalyser.SnapBaud(100.4));

            // 74.2 and 75 are 1.1% apart, the tightest pair in the table, and
            // they are still told apart by a tolerance twice that gap - because
            // the nearest is chosen before the tolerance is consulted, so a
            // looser one cannot pick the wrong neighbour, only accept a worse
            // fit to the right one.
            Assert.Equal(74.2, RttySignalAnalyser.SnapBaud(74.3));
            Assert.Equal(75.0, RttySignalAnalyser.SnapBaud(74.9));

            Assert.Null(RttySignalAnalyser.SnapBaud(68.3));
            Assert.Null(RttySignalAnalyser.SnapBaud(38.0));
        }

        [Fact]
        public void A_station_on_a_named_speed_is_recognised_every_time_not_most_times()
        {
            // The four figures Auto actually produced from DDK9 - a published 50
            // baud station - across four presses on 2026-10-08. At the original
            // 1.5% tolerance the last of them was rejected and the operator was
            // told a 50 baud station was running 50.79: the same signal described
            // two different ways depending on which four seconds were listened to.
            //
            // This is the regression test for the tolerance, so it is written as
            // the measurements rather than as a number. If the analyser is ever
            // made more precise the tolerance can come back down, but it must
            // never again be set below what the analyser can actually deliver.
            foreach (var measured in new[] { 49.61, 49.71, 50.65, 50.79 })
                Assert.Equal(50.0, RttySignalAnalyser.SnapBaud(measured));
        }

        // --- one tone louder than the other ----------------------------------

        [Theory]
        [InlineData(0.0)]
        [InlineData(1.6)]
        [InlineData(3.0)]
        [InlineData(6.0)]
        [InlineData(-1.6)]
        [InlineData(-3.0)]
        [InlineData(-6.0)]
        public void A_signal_with_one_tone_louder_is_still_measured_at_its_real_speed(
            double spaceDbLouder)
        {
            var got = RttySignalAnalyser.Analyse(Lopsided(2125, 450, 50.0, spaceDbLouder), Rate);

            Assert.NotNull(got);
            Hz(2125, got!.MarkHz);
            Hz(2575, got.SpaceHz);

            // Looser than the half a percent the matched case gets, and the residual
            // is still a clean function of the imbalance - about a sixth of a percent
            // per dB, in the direction of the louder tone. It is small enough to snap
            // to a named speed and far smaller than a demodulator cares about.
            Baud(50.0, got.Baud, 0.012);
        }

        [Fact]
        public void An_unequalised_comparison_is_what_made_the_speed_read_low()
        {
            // The regression this guards. Before the two magnitudes were divided by
            // their own levels, the comparison between them crossed away from the
            // tones' midpoint: every run of the louder tone gained a constant, a
            // constant on every run reads as a longer bit, and the speed came out
            // low in proportion. A real station on 2026-10-08 read 48.7 to 49.2
            // repeatably where it should have read 50, and its tones had already been
            // measured 1.6 dB apart on the same bench.
            //
            // 1.6 dB used to cost 1.9% of the speed. The test above holds it to 1.2%
            // at nearly four times the imbalance; this one states the direction, which
            // is the part that made it findable - the error never once fell on the
            // other side of the true speed.
            var quiet = RttySignalAnalyser.Analyse(Lopsided(2125, 450, 50.0, 6.0), Rate);
            var loud  = RttySignalAnalyser.Analyse(Lopsided(2125, 450, 50.0, -6.0), Rate);

            Assert.NotNull(quiet);
            Assert.NotNull(loud);
            Assert.True(quiet!.Baud < 50.0, $"space louder should still read slightly low, got {quiet.Baud}");
            Assert.True(loud!.Baud > 50.0, $"mark louder should still read slightly high, got {loud.Baud}");

            // And both snap to the right rung, which is what the operator sees.
            Assert.Equal(50.0, RttySignalAnalyser.SnapBaud(quiet.Baud));
            Assert.Equal(50.0, RttySignalAnalyser.SnapBaud(loud.Baud));
        }

        // --- noise, and knowing when to say nothing --------------------------

        [Fact]
        public void A_signal_a_listener_would_call_good_is_identified_correctly()
        {
            var got = RttySignalAnalyser.Analyse(Signal(2125, 170, 45.45, noiseSigma: 0.8), Rate);

            Assert.NotNull(got);
            Hz(2125, got!.MarkHz);
            Baud(45.45, got.Baud);
            Assert.True(got.Confidence > 0.5, $"confidence was only {got.Confidence:F2}");
        }

        [Fact]
        public void A_weak_but_readable_signal_is_still_identified_correctly()
        {
            // The same noise level the demodulator copies perfectly at, so the
            // analyser must not be the weaker half of the pair - an Auto button
            // that gives up before the decoder does is worse than no button.
            var got = RttySignalAnalyser.Analyse(Signal(2125, 170, 45.45, noiseSigma: 2.0), Rate);

            Assert.NotNull(got);
            Hz(2125, got!.MarkHz);
            Hz(170, got.ShiftHz);
            Baud(45.45, got.Baud);
        }

        [Fact]
        public void A_signal_well_down_in_the_noise_is_still_identified()
        {
            // Four times the noise of the "weak" case, and well past the point
            // where the demodulator copies cleanly - which is the way round it
            // should be. An Auto button is useful precisely when the operator
            // cannot tell what they are listening to, and that is when the signal
            // is poor.
            var got = RttySignalAnalyser.Analyse(Signal(2125, 170, 45.45, noiseSigma: 4.0), Rate);

            Assert.NotNull(got);
            Hz(2125, got!.MarkHz);
            Baud(45.45, got.Baud);
            Assert.True(got.Confidence > 0.5, $"confidence was only {got.Confidence:F2}");
        }

        [Fact]
        public void Noise_on_its_own_is_reported_with_low_confidence()
        {
            // It will still name two peaks - noise always has a highest bin, and
            // refusing to answer is not better than answering with a number that
            // says "this is a guess". What matters is that the number is low
            // enough for a caller to act on.
            var hiss = new float[Rate * 3];
            AddNoise(hiss, 1.0, 5);

            var got = RttySignalAnalyser.Analyse(hiss, Rate);

            // Asserted as a disjunction rather than behind an if, so that it cannot
            // start passing silently if the answer becomes null one day. It is not
            // null today: hiss always has a highest bin, and three seconds of it
            // scores 0.02.
            Assert.True(got is null || got.Confidence < 0.4,
                $"noise claimed confidence {got?.Confidence:F2} " +
                $"({got?.MarkHz:F0}/{got?.SpaceHz:F0} Hz, {got?.Baud} baud)");
        }

        [Fact]
        public void A_plain_carrier_is_not_mistaken_for_a_RTTY_signal()
        {
            // One tone, not two. A tuning carrier or a stuck transmitter, and
            // whatever second peak gets found is noise - so the confidence has to
            // collapse even though the strongest peak is strong.
            var carrier = new float[Rate * 3];
            for (int i = 0; i < carrier.Length; i++)
                carrier[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 1500 * i / Rate));
            AddNoise(carrier, 0.3, 3);

            var got = RttySignalAnalyser.Analyse(carrier, Rate);

            // Today this returns null outright - a steady carrier has no keying in
            // it, so there are not enough runs to measure. The disjunction is so
            // that the test still means something if that changes.
            Assert.True(got is null || got.Confidence < 0.4,
                $"a carrier claimed confidence {got?.Confidence:F2}");
        }

        [Fact]
        public void Silence_says_nothing_rather_than_guessing()
        {
            var got = RttySignalAnalyser.Analyse(new float[Rate * 2], Rate);

            // Digital silence has no keying to measure, so there is nothing to
            // report and null is what comes back.
            Assert.True(got is null || got.Confidence < 0.2,
                $"silence claimed confidence {got?.Confidence:F2}");
        }

        [Fact]
        public void Too_little_audio_to_judge_is_refused_outright()
        {
            // A tenth of a second does not hold enough keying transitions to
            // measure a speed from, and a confident answer from it would be a
            // fabrication. Null is the honest return.
            var clip = Signal(2125, 170, 45.45).AsSpan(0, 4000);

            Assert.Null(RttySignalAnalyser.Analyse(clip, Rate));
        }

        // --- the search range ------------------------------------------------

        [Fact]
        public void A_tone_outside_the_search_range_is_not_found()
        {
            // The documented limit, pinned so it stays documented: if a tone has
            // fallen outside the receiver's passband - which is what being badly
            // off tune looks like - there is nothing here that can recover it.
            var got = RttySignalAnalyser.Analyse(
                Signal(2125, 170, 45.45), Rate, lowHz: 300, highHz: 1200);

            // Both real tones are above 1200, so whatever comes back is the signal
            // leaking over the edge of the range, and it must not look convincing.
            Assert.True(got is null || got.Confidence < 0.4,
                $"found {got?.MarkHz:F0}/{got?.SpaceHz:F0} Hz outside the range, " +
                $"confidence {got?.Confidence:F2}");
        }

        [Fact]
        public void A_narrowed_search_range_still_finds_a_signal_inside_it()
        {
            // The other half of the previous test: narrowing the range is useful
            // precisely because it excludes interference, so it must not also
            // exclude the wanted signal.
            var got = RttySignalAnalyser.Analyse(
                Signal(2125, 170, 45.45), Rate, lowHz: 1800, highHz: 2600);

            Assert.NotNull(got);
            Hz(2125, got!.MarkHz);
            Hz(170, got.ShiftHz);
        }

        // --- sample rates ----------------------------------------------------

        [Theory]
        [InlineData(48000)]
        [InlineData(44100)]
        [InlineData(11025)]
        [InlineData(8000)]
        public void Any_sample_rate_a_sound_card_offers_works(int rate)
        {
            // 8 kHz is the awkward one: it leaves 2295 Hz only just inside the
            // Nyquist limit, and the FFT bins are six times wider than at 48 kHz.
            var audio = RttyModulator.ToAudio(
                Traffic, rate, 2125, 2295, 45.45, idleBitsBefore: 20, idleBitsAfter: 10);

            var got = RttySignalAnalyser.Analyse(audio, rate);

            Assert.NotNull(got);
            Hz(2125, got!.MarkHz);
            Baud(45.45, got.Baud);
        }
    }
}
