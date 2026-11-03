// LaunchPage.cs
namespace Yaesu_Web_Control.Services
{
    // A page named on the command line, so a desktop shortcut can open YWC
    // straight on it, e.g. Yaesu_Web_Control.exe rtty-contest (Bruce VK2RT,
    // Discussion #257). Only these names count; anything else is left to the
    // host's own command line.
    public static class LaunchPage
    {
        private static readonly Dictionary<string, string> Pages = new(StringComparer.OrdinalIgnoreCase)
        {
            ["rtty-contest"] = "/rtty-contest",
            ["radio"]        = "/radio",
            ["classic"]      = "/",
            ["experimental"] = "/flexui",
            ["settings"]     = "/Settings",
        };

        // The page's path, or null when none was named.
        public static string? Path { get; private set; }

        public static void ReadFrom(string[] args)
        {
            foreach (var arg in args)
            {
                var name = arg.TrimStart('-', '/');
                if (Pages.TryGetValue(name, out var path)) { Path = path; return; }
            }
        }

        // The URL to open: the named page under rootUrl, or rootUrl itself.
        public static string At(string rootUrl) =>
            Path is null or "/" ? rootUrl : rootUrl.TrimEnd('/') + Path;
    }
}
