namespace Yaesu_Web_Control.Services;

/// <summary>
/// Detects container / headless hosting so CAT-only Linux images can skip
/// desktop behaviours (auto-open browser, exit-when-no-tabs).
/// </summary>
public static class HostRuntime
{
    public static bool IsContainer { get; } =
        string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase)
        || File.Exists("/.dockerenv");

    /// <summary>
    /// True when YWC_DEVELOPER=1 is set on this PC. Turns on the hidden
    /// Developer section in Settings (bench tools users never see).
    /// </summary>
    public static bool IsDeveloper { get; } =
        Environment.GetEnvironmentVariable("YWC_DEVELOPER") == "1";
}
