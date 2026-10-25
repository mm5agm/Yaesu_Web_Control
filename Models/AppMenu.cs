namespace Yaesu_Web_Control.Models
{
    /// <summary>
    /// The app's other pages, as the Menu dropdown lists them in every view
    /// except Classic (Pages/Shared/_SiteMenuPartial.cshtml, and the Radio
    /// panel's MENU key). Classic keeps them as plain links in its nav bar,
    /// for screen-reader users. Add a page here and every view's Menu has it.
    /// </summary>
    public static class AppMenu
    {
        public sealed record Page(string Label, string Path, string Icon);

        public sealed record Group(string Name, IReadOnlyList<Page> Pages);

        public static readonly IReadOnlyList<Group> Groups = new[]
        {
            new Group("Setup", new[]
            {
                new Page("Settings", "/Settings", "bi-gear-fill"),
                new Page("Application Setup", "/ApplicationSetup", "bi-sliders"),
                new Page("Memories", "/Memories", "bi-bookmarks-fill"),
                new Page("Accessibility Labels", "/Labels", "bi-universal-access"),
                new Page("Meter Calibration", "/MeterCalibration", "bi-speedometer2"),
                new Page("Diagnostics", "/Diagnostics", "bi-activity"),
            }),
            new Group("Help", new[]
            {
                new Page("User Manual", "/UserManual", "bi-book-fill"),
                new Page("About", "/About", "bi-info-circle-fill"),
            }),
        };
    }
}
