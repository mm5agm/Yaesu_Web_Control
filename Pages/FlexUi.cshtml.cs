using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    /// <summary>
    /// Flex UI workspace — the same server state as Index, presented through
    /// dockable FlexLayout tabs. The classic Index page is unchanged; this is
    /// an alternative surface at /flexui.
    /// </summary>
    public class FlexUiModel : IndexModel
    {
        private FlexViewState? _view;

        /// <summary>Server-computed flags shared with Pages/FlexPartials/*.</summary>
        public FlexViewState View => _view ??= FlexViewState.Build(this);

        public FlexUiModel(RadioStateService radioStateService, ISettingsService settingsService)
            : base(radioStateService, settingsService)
        {
        }
    }
}
