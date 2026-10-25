namespace Yaesu_Web_Control.Models
{
    /// <summary>
    /// The whole-page views the operator picks between in the nav bar's View
    /// dropdown. The one last opened is saved as
    /// <see cref="ApplicationSettings.LastView"/>, and <c>/start</c> - which
    /// the app opens at start-up and from the tray - redirects to it.
    /// </summary>
    public static class AppViews
    {
        public const string Classic = "classic";
        public const string Experimental = "experimental";
        public const string Radio = "radio";

        public sealed record View(string Key, string Label, string Path, string Icon, string Title);

        public static readonly IReadOnlyList<View> All = new[]
        {
            new View(Classic, "Classic", "/", "bi-house-door-fill", "The original control page"),
            new View(Experimental, "Experimental", "/flexui", "bi-grid-1x2-fill", "The same controls, in a dockable workspace"),
            new View(Radio, "Radio", "/radio", "bi-broadcast", "Radio-style front panel"),
        };

        /// <summary>The view with this key; Classic for an unknown or empty one.</summary>
        public static View For(string? key) =>
            All.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase)) ?? All[0];

        /// <summary>The view served at this request path, or null on any other page.</summary>
        public static View? ForPath(string? path)
        {
            var p = (path ?? "").TrimEnd('/');
            if (p.Length == 0 || p.Equals("/Index", StringComparison.OrdinalIgnoreCase)) return All[0];
            return All.Skip(1).FirstOrDefault(v => p.Equals(v.Path, StringComparison.OrdinalIgnoreCase));
        }
    }
}
