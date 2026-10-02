using Xunit;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// SD is a two-digit step number on the FTdx101/FTdx10/FT-710 and
    /// four-digit ms on the FTDX3000. Until 2026-10-02 YWC sent ms to all of
    /// them, so a delay set from the app never reached a '101.
    /// </summary>
    public class CwBreakInCodesTests
    {
        [Theory]
        [InlineData("FTdx101MP", 30, "SD00;")]
        [InlineData("FTdx101MP", 200, "SD04;")]
        [InlineData("FTdx101MP", 300, "SD06;")]
        [InlineData("FTdx101MP", 700, "SD10;")]
        [InlineData("FTdx101MP", 3000, "SD33;")]
        [InlineData("FTdx10", 120, "SD02;")]    // nearest step is 100
        [InlineData("FT-710", 650, "SD09;")]    // 600/700 tie goes to the lower
        [InlineData("FTDX3000", 700, "SD0700;")]
        [InlineData("FTDX3000", 5, "SD0030;")]  // clamped to the radio's minimum
        public void SetDelayCommand_uses_each_models_format(string model, int ms, string expected)
            => Assert.Equal(expected, CwBreakInCodes.SetDelayCommand(model, ms));

        [Theory]
        [InlineData("SD04;", 200)]
        [InlineData("SD00;", 30)]
        [InlineData("SD07;", 400)]
        [InlineData("SD33;", 3000)]
        [InlineData("SD0700;", 700)]
        public void ParseDelayMs_reads_both_forms(string answer, int expected)
            => Assert.Equal(expected, CwBreakInCodes.ParseDelayMs(answer));

        [Theory]
        [InlineData("SD34;")]
        [InlineData("SD;")]
        [InlineData("SDxx;")]
        [InlineData("BI1;")]
        public void ParseDelayMs_rejects_what_it_cannot_read(string answer)
            => Assert.Null(CwBreakInCodes.ParseDelayMs(answer));

        [Fact]
        public void Every_step_round_trips()
        {
            for (int i = 0; i <= 33; i++)
            {
                int ms = CwBreakInCodes.ParseDelayMs($"SD{i:D2};")!.Value;
                Assert.Equal($"SD{i:D2};", CwBreakInCodes.SetDelayCommand("FTdx101MP", ms));
            }
        }

        [Theory]
        [InlineData("FTdx101MP", true)]
        [InlineData("FTdx101D", true)]
        [InlineData("FTdx10", false)]   // menu address not yet read on a radio
        [InlineData("FT-710", false)]
        [InlineData("FTDX3000", false)]
        public void Full_break_in_only_where_the_menu_address_is_confirmed(string model, bool expected)
            => Assert.Equal(expected, CwBreakInCodes.SupportsFullBreakIn(model));
    }
}
