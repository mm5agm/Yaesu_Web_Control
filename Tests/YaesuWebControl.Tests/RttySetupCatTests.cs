using System.Linq;
using Xunit;
using Yaesu_Web_Control.Services.Rtty;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// The operator's RTTY setup string (Bruce VK2RT, Discussion #250): what is
    /// sent for which band, and that nothing which could transmit or write a
    /// memory gets through.
    /// </summary>
    public class RttySetupCatTests
    {
        // Bruce's own 20 m string, as he posted it.
        private const string Bruce20m =
            "NR00;MD08;EX0305020;EX01040601;EX0104070;EX01040867;EX0104090;EX0104100;EX0104120;EX0104131;" +
            "EX0104141;EX0104161;RF02;PC005;ML0001;ML1025;SH0010;LK0;FA014090000;EX0402020;SS0000000;" +
            "SS0100000;SS0200000;SS04+20.0;SS0530000;SS0650000;SS0800000;";

        [Fact]
        public void Bruces_string_is_sent_whole_and_in_order()
        {
            var (commands, problems) = RttySetupCat.For(Bruce20m, "20m");
            Assert.Empty(problems);
            Assert.Equal(27, commands.Count);
            Assert.Equal("NR00", commands[0]);
            Assert.Contains("SS04+20.0", commands);
            Assert.Equal("SS0800000", commands[^1]);
        }

        [Fact]
        public void A_band_line_goes_only_with_its_band_after_the_common_ones()
        {
            const string text = "20m: FA014090000;\r\nMD08; SH0010;\n40m: FA007040000;";
            Assert.Equal(new[] { "MD08", "SH0010", "FA014090000" }, RttySetupCat.For(text, "20m").Commands);
            Assert.Equal(new[] { "MD08", "SH0010", "FA007040000" }, RttySetupCat.For(text, "40M").Commands);
            Assert.Equal(new[] { "MD08", "SH0010" }, RttySetupCat.For(text, null).Commands);
        }

        [Theory]
        [InlineData("TX1;")]
        [InlineData("MX1;")]
        [InlineData("VX1;")]
        [InlineData("KYCQ TEST;")]
        [InlineData("AC002;")]
        [InlineData("PB01;")]
        [InlineData("MW00100;")]
        [InlineData("ps0;")]
        public void A_command_that_could_transmit_or_write_refuses_the_whole_setup(string bad)
        {
            var (_, problems) = RttySetupCat.For("MD08;" + bad + "SH0010;", "20m");
            Assert.Single(problems);
            Assert.Contains("not allowed", problems[0]);
        }

        [Fact]
        public void Text_that_is_not_a_command_is_a_problem()
        {
            var (_, problems) = RttySetupCat.For("MD08; 12AB; FA0140#;", null);
            Assert.Equal(2, problems.Count);
        }

        // AUTO on WIDTH goes back to this width, not the tuner's own (Bruce
        // VK2RT, #250 2026-11-01: 300 Hz where his setup has SH0010, 500 Hz).
        [Fact]
        public void The_setup_width_is_Bruces_SH_code_for_MAIN()
        {
            Assert.Equal("10", RttySetupCat.WidthCode(Bruce20m, "20m", "FTdx101MP", "0"));
            Assert.Null(RttySetupCat.WidthCode(Bruce20m, "20m", "FTdx101MP", "1"));
        }

        [Fact]
        public void The_last_SH_for_the_band_wins_and_no_SH_means_no_setup_width()
        {
            const string text = "SH0010;\n20m: SH0007;";
            Assert.Equal("07", RttySetupCat.WidthCode(text, "20m", "FTdx101MP", "0"));
            Assert.Equal("10", RttySetupCat.WidthCode(text, "40m", "FTdx101MP", "0"));
            Assert.Null(RttySetupCat.WidthCode("MD08;", "20m", "FTdx101MP", "0"));
            Assert.Null(RttySetupCat.WidthCode(null, "20m", "FTdx101MP", "0"));
        }

        [Fact]
        public void A_setup_that_would_not_be_sent_gives_no_width()
            => Assert.Null(RttySetupCat.WidthCode("SH0010; TX1;", "20m", "FTdx101MP", "0"));

        [Fact]
        public void The_FT991A_SH_has_no_fixed_zero()
        {
            Assert.Equal("10", RttySetupCat.WidthCode("SH010;", "20m", "FT-991A", "0"));
            Assert.Null(RttySetupCat.WidthCode("SH0010;", "20m", "FT-991A", "0"));
        }
    }

    public class LaunchPageTests
    {
        [Fact]
        public void A_named_page_is_opened_under_the_root()
        {
            Yaesu_Web_Control.Services.LaunchPage.ReadFrom(new[] { "--urls=x", "RTTY-Contest" });
            Assert.Equal("https://localhost:8443/rtty-contest",
                         Yaesu_Web_Control.Services.LaunchPage.At("https://localhost:8443/"));
        }
    }
}
