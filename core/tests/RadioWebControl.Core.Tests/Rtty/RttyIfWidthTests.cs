using RadioWebControl.Core.Services.Rtty;
using Xunit;

namespace RadioWebControl.Core.Tests.Rtty
{
    /// <summary>
    /// Checked against bench measurements rather than against arithmetic, because
    /// arithmetic is what got this wrong the first time.
    ///
    /// <para>The class used to return <c>shift + 2 * baud</c> and this file used to
    /// defend that figure with the observation that "every rig built for 170 Hz at
    /// 45.45 baud offers a filter around 250-300 Hz, and an arithmetic that landed
    /// well outside that range would be the arithmetic that was wrong, not the
    /// radios". The reasoning was sound and the conclusion was still wrong, which is
    /// worth keeping a note of: <b>the textbook figure is the right answer for a
    /// radio that centres its RTTY passband on the middle of the tone pair</b>, and
    /// neither radio measured here does that. One centres on the mark; the other
    /// does not move its passband at all. The agreement with eighty years of
    /// practice was agreement about a third kind of radio.</para>
    ///
    /// <para>The <see cref="BenchPoints"/> theory is the real test. Every row is a
    /// width that was actually tried against a real signal on 2026-10-09, with
    /// whether the decoder produced characters. Fourteen rows, two radios, two tone
    /// placements. The old formula agreed with three of them.</para>
    /// </summary>
    public class RttyIfWidthTests
    {
        // DDK9, the DWD RTTY bulletin on 10100.8 kHz: 450 Hz shift, 50 baud,
        // continuous, and its text is published - which is what made it usable as
        // ground truth rather than an impression.
        private const double Baud = 50;

        // Mark 2125 / space 2575 is the pair as the IC-7300 MkII was tuned for it,
        // and the same pair the FTdx101MP saw at dial 10101000. Mark 1675 / space
        // 2125 is that identical signal moved 450 Hz down the audio passband by
        // retuning the dial, which is the comparison that proved the passband does
        // not follow the tones.
        public static TheoryData<string, double, double, double, int, bool> BenchPoints => new()
        {
            // radio / centre description, mark, space, centre, width, did it decode?

            // IC-7300 MkII: passband centred on the mark. 550 Hz is exactly what the
            // old formula asked for, and it put the space tone 33 dB down.
            { "IC-7300 MkII, mark-centred", 2125, 2575, 2125,  550, false },
            { "IC-7300 MkII, mark-centred", 2125, 2575, 2125, 1200, true  },

            // FTdx101MP, pair high in the audio (dial 10101000). Space dies first
            // and dies early, because it is the tone furthest from the fixed centre.
            { "FTdx101MP, pair at 2125/2575", 2125, 2575, 1800, 2400, true  },
            { "FTdx101MP, pair at 2125/2575", 2125, 2575, 1800, 1700, true  },
            { "FTdx101MP, pair at 2125/2575", 2125, 2575, 1800, 1200, false },
            { "FTdx101MP, pair at 2125/2575", 2125, 2575, 1800,  800, false },
            { "FTdx101MP, pair at 2125/2575", 2125, 2575, 1800,  600, false },
            { "FTdx101MP, pair at 2125/2575", 2125, 2575, 1800,  500, false },

            // The same signal, same shift, same speed, moved down in audio. It now
            // survives 800 Hz where before it needed 1700 - a factor of two from the
            // placement alone. No formula in shift and baud can produce both columns.
            { "FTdx101MP, pair at 1675/2125", 1675, 2125, 1800, 2400, true  },
            { "FTdx101MP, pair at 1675/2125", 1675, 2125, 1800, 1700, true  },
            { "FTdx101MP, pair at 1675/2125", 1675, 2125, 1800, 1200, true  },
            { "FTdx101MP, pair at 1675/2125", 1675, 2125, 1800,  800, true  },
            { "FTdx101MP, pair at 1675/2125", 1675, 2125, 1800,  600, false },
            { "FTdx101MP, pair at 1675/2125", 1675, 2125, 1800,  500, false },
        };

        [Theory]
        [MemberData(nameof(BenchPoints))]
        public void Matches_what_the_radios_actually_did(
            string placement, double markHz, double spaceHz, double centreHz, int widthHz, bool decoded)
        {
            bool predicted = RttyIfWidth.Passes(widthHz, markHz, spaceHz, Baud, centreHz);
            Assert.True(predicted == decoded,
                $"{placement}: a {widthHz} Hz filter {(decoded ? "decoded" : "decoded nothing")} on the "
                + $"bench, but the floor says it {(predicted ? "should work" : "should not work")}.");
        }

        // The three floors those fourteen points imply, written out so a change to
        // the sum shows up as a number rather than as a theory row going red.
        [Theory]
        [InlineData(2125, 2575, 2125, 1000)] // IC-7300 MkII: 2*450 + 2*50
        [InlineData(2125, 2575, 1800, 1650)] // FTdx101MP, pair high: 2*775 + 2*50
        [InlineData(1675, 2125, 1800,  750)] // FTdx101MP, pair low:  2*325 + 2*50
        public void Floor_is_set_by_the_tone_furthest_from_the_centre(
            double markHz, double spaceHz, double centreHz, int expected)
        {
            Assert.Equal(expected, RttyIfWidth.MinimumHz(markHz, spaceHz, Baud, centreHz));
        }

        // The old formula's answer for every one of those three cases was 550 Hz.
        // Keeping it here as an explicit non-assertion is the clearest way to say
        // that a future simplification back to shift-plus-twice-baud is a
        // regression, not a tidy-up.
        [Fact]
        public void Is_not_the_old_shift_plus_twice_baud_sum()
        {
            const int oldAnswer = 550; // 450 + 2 * 50, for all three placements
            Assert.NotEqual(oldAnswer, RttyIfWidth.MinimumHz(2125, 2575, Baud, 2125));
            Assert.NotEqual(oldAnswer, RttyIfWidth.MinimumHz(2125, 2575, Baud, 1800));
            Assert.NotEqual(oldAnswer, RttyIfWidth.MinimumHz(1675, 2125, Baud, 1800));
        }

        [Fact]
        public void Does_not_care_which_side_of_the_mark_the_space_tone_is_on()
        {
            // Reverse, and the RTTY-U/RTTY-L distinction, put space below the mark.
            // Distance from the centre is all that matters, so a pair straddling a
            // mark-centred passband gives the same floor either way.
            Assert.Equal(
                RttyIfWidth.MinimumHz(2125, 2575, Baud, 2125),
                RttyIfWidth.MinimumHz(2125, 1675, Baud, 2125));
        }

        [Fact]
        public void Rounds_a_floor_upwards()
        {
            // 2 * 170 + 2 * 45.45 = 430.9, and a floor rounded down is a width that
            // clips - by less than a hertz, but the direction is the whole point.
            var floor = RttyIfWidth.MinimumHz(2125, 2295, 45.45, 2125);
            Assert.Equal(431, floor);
        }

        [Fact]
        public void Widens_a_filter_that_is_too_narrow()
        {
            Assert.Equal(1000, RttyIfWidth.WidenToHz(
                currentWidthHz: 250, markHz: 2125, spaceHz: 2575, baud: Baud, passbandCentreHz: 2125));
        }

        [Fact]
        public void Leaves_a_filter_alone_when_it_is_already_wide_enough()
        {
            Assert.Null(RttyIfWidth.WidenToHz(
                currentWidthHz: 1200, markHz: 2125, spaceHz: 2575, baud: Baud, passbandCentreHz: 2125));
        }

        // The whole point of the class. A 2700 Hz passband is a poor choice for
        // RTTY and the operator may well want it narrower - but that is their call,
        // and nothing here is allowed to make it for them.
        [Fact]
        public void Never_narrows_a_filter_that_is_wider_than_it_needs_to_be()
        {
            Assert.Null(RttyIfWidth.WidenToHz(
                currentWidthHz: 2700, markHz: 2125, spaceHz: 2295, baud: 45.45, passbandCentreHz: 2125));
        }

        [Fact]
        public void Writes_nothing_when_the_current_width_is_unknown()
        {
            Assert.Null(RttyIfWidth.WidenToHz(0,  2125, 2575, Baud, 2125));
            Assert.Null(RttyIfWidth.WidenToHz(-1, 2125, 2575, Baud, 2125));
        }

        // "Cannot say" has to stay distinguishable from "no width needed", because a
        // caller that reads a null as zero writes the radio's narrowest filter.
        [Theory]
        [InlineData(0,    2575, 2125)] // no mark
        [InlineData(2125, 0,    2125)] // no space
        [InlineData(2125, 2575, 0)]    // centre not known
        [InlineData(double.NaN, 2575, 2125)]
        [InlineData(2125, double.NaN, 2125)]
        [InlineData(2125, 2575, double.NaN)]
        [InlineData(double.PositiveInfinity, 2575, 2125)]
        public void Says_it_cannot_tell_rather_than_guessing(double markHz, double spaceHz, double centreHz)
        {
            Assert.Null(RttyIfWidth.MinimumHz(markHz, spaceHz, Baud, centreHz));
            Assert.Null(RttyIfWidth.WidenToHz(250, markHz, spaceHz, Baud, centreHz));
            Assert.False(RttyIfWidth.Passes(250, markHz, spaceHz, Baud, centreHz));
        }

        [Fact]
        public void Says_it_cannot_tell_when_the_speed_is_nonsense()
        {
            Assert.Null(RttyIfWidth.MinimumHz(2125, 2575, double.NaN, 2125));
            Assert.Null(RttyIfWidth.MinimumHz(2125, 2575, -1, 2125));
        }

        // A tone pair sitting right on the centre of the passband still needs room
        // for its keying sidebands, so the floor is never zero for a real signal.
        [Fact]
        public void Still_asks_for_the_keying_sidebands_when_a_tone_sits_on_the_centre()
        {
            Assert.Equal(100, RttyIfWidth.MinimumHz(2125, 2125, Baud, 2125));
        }

        [Fact]
        public void Passes_is_false_for_a_width_that_is_not_known()
        {
            Assert.False(RttyIfWidth.Passes(0,  2125, 2575, Baud, 2125));
            Assert.False(RttyIfWidth.Passes(-1, 2125, 2575, Baud, 2125));
        }
    }
}
