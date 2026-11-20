namespace YaesuWebControl.Tests;

/// <summary>
/// In Docker the app launcher buttons can't start anything (no desktop, none of
/// the user's programs), so they are hidden there and Application Setup says why.
/// Reported by a Linux Docker user on groups.io, 2026-11-20.
/// </summary>
public class DockerAppButtonsTests
{
    [Fact]
    public void Every_app_button_is_off_in_a_container()
    {
        var index = Read("Pages/Index.cshtml.cs");
        Assert.Contains("var canLaunchApps = !HostRuntime.IsContainer;", index);
        foreach (var (n, setting) in new[] { (1, "Wsjtx"), (2, "Jtalert"), (3, "Log4om"), (4, "Gridtracker"), (5, "Fldigi") })
            Assert.Contains($"ShowApp{n}Button = canLaunchApps && settings.Show{setting}Button;", index);
    }

    [Fact]
    public void The_flex_page_takes_its_buttons_from_the_same_model()
        => Assert.Contains("public class FlexUiModel : IndexModel", Read("Pages/FlexUi.cshtml.cs"));

    [Fact]
    public void Application_setup_explains_it_in_a_container()
    {
        Assert.Contains("public bool InContainer => HostRuntime.IsContainer;", Read("Pages/ApplicationSetup.cshtml.cs"));
        var page = Read("Pages/ApplicationSetup.cshtml");
        Assert.Contains("@if (Model.InContainer)", page);
        Assert.Contains("id=\"appButtonsDockerNote\"", page);
    }

    private static string Read(string path) => File.ReadAllText(Path.Combine(RepoRoot(), path));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Yaesu_Web_Control.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException($"No Yaesu_Web_Control.csproj above {AppContext.BaseDirectory}.");
    }
}
