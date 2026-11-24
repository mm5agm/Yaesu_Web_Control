using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests;

/// <summary>
/// Clusters that ask for a password after the callsign (KE1AB, 2026-11-24).
/// The prompt must get the password, never the callsign, and the password
/// must survive a Settings save (password boxes drop their value otherwise).
/// </summary>
public class DxClusterPasswordTests
{
    [Theory]
    [InlineData("password:")]
    [InlineData("Password: ")]
    [InlineData("Please enter your password:")]
    [InlineData("Enter password")]
    [InlineData("password?")]
    public void Password_prompts_are_seen(string line)
        => Assert.True(DxClusterService.IsPasswordPrompt(line));

    [Theory]
    [InlineData("login:")]
    [InlineData("Please enter your call:")]
    [InlineData("DX de G4ABC:     14025.0  K1ABC        forgot my password again, sorry   1234Z")]
    [InlineData("")]
    public void Other_lines_are_not(string line)
        => Assert.False(DxClusterService.IsPasswordPrompt(line));

    [Fact]
    public void The_settings_box_keeps_its_value()
    {
        var page = File.ReadAllText(Path.Combine(RepoRoot(), "Pages/Settings.cshtml"));
        Assert.Contains("value=\"@Model.Settings.DxClusterPassword\"", page);
        var model = File.ReadAllText(Path.Combine(RepoRoot(), "Pages/Settings.cshtml.cs"));
        Assert.Contains("current.DxClusterPassword", model);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Yaesu_Web_Control.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException($"No Yaesu_Web_Control.csproj above {AppContext.BaseDirectory}.");
    }
}
