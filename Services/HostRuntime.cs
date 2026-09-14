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
}
