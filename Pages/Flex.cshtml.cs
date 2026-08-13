using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    /// <summary>
    /// FlexLayout workspace — same server state as Index, separate UI surface.
    /// </summary>
    public class FlexModel : IndexModel
    {
        private FlexViewState? _view;

        /// <summary>Computed Razor variables shared with FlexPartials/*.</summary>
        public FlexViewState View => _view ??= FlexViewState.Build(this);

        public FlexModel(RadioStateService radioStateService, ISettingsService settingsService)
            : base(radioStateService, settingsService)
        {
        }
    }
}
