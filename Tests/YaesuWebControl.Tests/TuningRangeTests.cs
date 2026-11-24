using System.Text.RegularExpressions;

namespace YaesuWebControl.Tests;

/// <summary>
/// Tuning paths take the top frequency from the radio's range
/// (RadioCapabilities.FrequencyRangeHz: 470 MHz on the FT-991A), not a fixed
/// 75 MHz. With a fixed cap, every wheel notch on the main page's SDR
/// spectrum sent a 2 m FT-991 to 75 MHz (D#404).
/// </summary>
public class TuningRangeTests
{
    [Theory]
    [InlineData("wwwroot/js/sdr/spectrum-panel.js")]
    [InlineData("wwwroot/js/flexui/app/sdr/spectrum-panel.js")]
    [InlineData("Services/Voice/IntentDispatcher.cs")]
    public void NoFixedSeventyFiveMegahertzCap(string file)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), file));
        Assert.DoesNotMatch(new Regex(@"Math\.min\(\s*75_000_000|>\s*75_000_000"), text);
    }

    [Theory]
    [InlineData("wwwroot/js/sdr/spectrum-panel.js")]
    [InlineData("wwwroot/js/flexui/app/sdr/spectrum-panel.js")]
    public void SpectrumWheelAndDragUseThePagesMaxHz(string file)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), file));
        Assert.Contains("dataset?.maxHz", text);
        Assert.Equal(2, Regex.Matches(text, @"Math\.min\(tuneMaxHz\(\)").Count);
    }

    [Fact]
    public void SpectrumWindowPassesMaxHzToTheDialStepper()
    {
        var root = RepoRoot();
        Assert.Contains("maxHz: @Model.MaxHz", File.ReadAllText(Path.Combine(root, "Pages/RadioSpectrum.cshtml")));
        Assert.Contains("maxHz: cfg.maxHz", File.ReadAllText(Path.Combine(root, "wwwroot/js/ui/radio-spectrum-yaesu.js")));
    }

    [Fact]
    public void VideoPageDigitsUseTheRadiosMaxHz()
    {
        var root = RepoRoot();
        var hotspots = File.ReadAllText(Path.Combine(root, "wwwroot/js/video/radio-display-hotspots.js"));
        Assert.Contains("to > this.maxHz", hotspots);
        Assert.DoesNotContain("to > FREQ_MAX_HZ", hotspots);
        foreach (var ui in new[] { "wwwroot/js/video/radio-display-ui.js", "wwwroot/js/flexui/app/video/radio-display-ui.js" })
            Assert.Contains("maxHz: Number(container.dataset.maxHz)", File.ReadAllText(Path.Combine(root, ui)));
        foreach (var page in new[] { "Pages/Index.cshtml", "Pages/RadioDisplay.cshtml", "Pages/FlexPartials/_RadioDisplay.cshtml" })
            Assert.Matches(new Regex(@"id=""radioDisplayContainer""[^>]*data-max-hz="), File.ReadAllText(Path.Combine(root, page)));
        Assert.Contains("dualReceiver: dual, maxHz,", File.ReadAllText(Path.Combine(root, "wwwroot/js/ui/radio-panel-yaesu.js")));
    }

    [Fact]
    public void FT991AReachesSeventyCentimetres()
    {
        Assert.Equal(470_000_000, Yaesu_Web_Control.Services.RadioCapabilities.FrequencyRangeHz("FT-991A").MaxHz);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Yaesu_Web_Control.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException($"No Yaesu_Web_Control.csproj above {AppContext.BaseDirectory}.");
    }
}
