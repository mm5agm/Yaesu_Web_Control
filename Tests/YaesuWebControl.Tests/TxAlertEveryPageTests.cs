namespace YaesuWebControl.Tests;

/// <summary>
/// The high SWR / high ALC pop-up (Discussion #257) is on every page, pop-out
/// windows included, so whichever one the operator has open shows it. Layout
/// pages carry it for the pages that use them; a page with its own body must
/// load it itself. A new standalone page fails here until it does. The module
/// doesn't start inside a frame, so framed pages don't show it twice.
/// </summary>
public class TxAlertEveryPageTests
{
    private const string Module = "/js/txalert/tx-alert.js";

    public static TheoryData<string> Pages()
    {
        var data = new TheoryData<string>();
        var root = RepoRoot();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "Pages"), "*.cshtml", SearchOption.AllDirectories))
            if (File.ReadAllText(f).Contains("<body", StringComparison.OrdinalIgnoreCase))
                data.Add(Path.GetRelativePath(root, f).Replace('\\', '/'));
        return data;
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void Page_with_its_own_body_loads_the_alert(string page)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), page));
        Assert.True(text.Contains(Module), $"{page} has its own <body> but doesn't load {Module}.");
    }

    [Fact]
    public void Module_is_in_core()
    {
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "core", "js", "txalert", "tx-alert.js")));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Yaesu_Web_Control.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException($"No Yaesu_Web_Control.csproj above {AppContext.BaseDirectory}.");
    }
}
