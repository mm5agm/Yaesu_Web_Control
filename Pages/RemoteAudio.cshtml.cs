using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Yaesu_Web_Control.Pages
{
    /// <summary>
    /// Compact pop-out host for the Remote Audio WebSocket session so Index
    /// navigation (e.g. Settings) does not tear down RX/TX audio.
    /// </summary>
    public class RemoteAudioModel : PageModel
    {
        public void OnGet()
        {
        }
    }
}
