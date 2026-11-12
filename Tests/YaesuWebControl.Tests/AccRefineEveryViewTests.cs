using System.Text.RegularExpressions;

namespace YaesuWebControl.Tests;

/// <summary>
/// Every view that draws the SDR spectrum has the Refine control for the
/// FTdx101's ACC scope (Colin, 2026-11-12: "If I do another view I will want
/// it in there as well"). They all get it from wwwroot/js/sdr/acc-refine.js,
/// so this looks for that module in every page and view script that draws a
/// spectrum. A new view fails here until it has Refine too.
/// </summary>
public class AccRefineEveryViewTests
{
    // What a file that draws the spectrum has in it: the hub's frames, the
    // shared feed, Classic's panel, or the radio panel kit.
    private static readonly Regex DrawsSpectrum = new(
        @"SpectrumUpdate|startYaesuSpectrumFeed|new\s+SpectrumPanel\b|mountRadioPanel|mountContestView|mountSpectrumPopout",
        RegexOptions.Compiled);

    // Plumbing every view uses, not views: they carry frames and have no controls.
    private static readonly string[] NotViews =
    {
        "wwwroot/js/sdr/sdr-spectrum-pipeline.js",
        "wwwroot/js/ui/spectrum-feed-yaesu.js",
        "wwwroot/js/sdr/acc-refine.js",
    };

    public static TheoryData<string> Views()
    {
        var data = new TheoryData<string>();
        foreach (var v in ViewFiles()) data.Add(v);
        return data;
    }

    private static IEnumerable<string> ViewFiles()
    {
        var root = RepoRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "Pages"), "*.cshtml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "wwwroot", "js", "ui"), "*.js"))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "wwwroot", "js", "sdr"), "*.js"));
        foreach (var f in files)
        {
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (NotViews.Contains(rel)) continue;
            if (DrawsSpectrum.IsMatch(File.ReadAllText(f))) yield return rel;
        }
    }

    [Fact]
    public void The_views_we_know_of_are_found()
    {
        var found = ViewFiles().ToHashSet();
        foreach (var v in new[]
        {
            "Pages/Index.cshtml",                       // Classic
            "Pages/FlexPartials/_FlexScripts.cshtml",   // Experimental
            "wwwroot/js/ui/radio-panel-yaesu.js",       // Radio
            "wwwroot/js/ui/radio-spectrum-yaesu.js",    // Radio's pop-out spectrum
            "wwwroot/js/ui/rtty-contest-yaesu.js",      // RTTY Contest and its windows
        })
            Assert.Contains(v, found);
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void A_view_that_draws_the_spectrum_has_refine(string view)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), view));
        Assert.True(text.Contains("/js/sdr/acc-refine.js"),
            $"{view} draws the spectrum but has no Refine control. Add it from /js/sdr/acc-refine.js " +
            "(bindRefineSlider for a slider, addRefineTo for a list beside SPAN).");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Yaesu_Web_Control.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException($"No Yaesu_Web_Control.csproj above {AppContext.BaseDirectory}.");
    }
}
