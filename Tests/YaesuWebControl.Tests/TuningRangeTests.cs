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
    [InlineData("Services/Voice/IntentDispatcher.cs")]
    public void NoFixedSeventyFiveMegahertzCap(string file)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), file));
        Assert.DoesNotMatch(new Regex(@"Math\.min\(\s*75_000_000|>\s*75_000_000"), text);
    }

    [Fact]
    public void SpectrumWheelAndDragUseThePagesMaxHz()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "wwwroot/js/sdr/spectrum-panel.js"));
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
