namespace YaesuWebControl.Tests;

/// <summary>
/// The hidden Developer section in Settings (2026-11-26). Bench tools such as
/// the Radio screen zone boxes live there and must never show for users:
/// the whole section sits inside the YWC_DEVELOPER=1 check.
/// </summary>
public class DeveloperSectionTests
{
    [Fact]
    public void The_developer_section_only_renders_for_the_developer()
    {
        var page = File.ReadAllText(Path.Combine(RepoRoot(), "Pages/Settings.cshtml"));
        var guard = page.IndexOf("@if (Model.IsDeveloper)", StringComparison.Ordinal);
        var section = page.IndexOf("id=\"developerSection\"", StringComparison.Ordinal);
        Assert.True(guard >= 0, "Settings.cshtml has no IsDeveloper check.");
        Assert.True(section > guard, "The Developer section must sit inside the IsDeveloper check.");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "id=\"developerSection\""));
    }

    [Fact]
    public void The_zone_boxes_switch_uses_the_hotspots_flag()
    {
        var page = File.ReadAllText(Path.Combine(RepoRoot(), "Pages/Settings.cshtml"));
        var js = File.ReadAllText(Path.Combine(RepoRoot(), "wwwroot/js/video/radio-display-hotspots.js"));
        const string key = "ywc.radioDisplayHotspots.debug";
        Assert.Contains(key, js);
        Assert.Contains(key, page);
    }

    [Fact]
    public void The_flag_comes_from_YWC_DEVELOPER()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "Services/HostRuntime.cs"));
        Assert.Contains("GetEnvironmentVariable(\"YWC_DEVELOPER\") == \"1\"", src);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Yaesu_Web_Control.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException($"No Yaesu_Web_Control.csproj above {AppContext.BaseDirectory}.");
    }
}
