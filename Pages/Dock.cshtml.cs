using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    /// <summary>
    /// Dockview workspace — same server state as Index, separate UI surface.
    /// </summary>
    public class DockModel : IndexModel
    {
        private DockViewState? _view;

        /// <summary>Computed Razor variables shared with DockPartials/*.</summary>
        public DockViewState View => _view ??= DockViewState.Build(this);

        public DockModel(RadioStateService radioStateService, ISettingsService settingsService)
            : base(radioStateService, settingsService)
        {
        }
    }
}
