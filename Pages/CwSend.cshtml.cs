using Microsoft.AspNetCore.Mvc.RazorPages;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    // What _CwSendPartial needs: which page it is in, and the keyer speed to
    // show before the first SignalR update arrives.
    public record CwSendPartialModel(bool Popout, int CwSpeed);

    // CW Send in a window of its own, so it can go on a second monitor beside
    // the CW reader. The sending is done in the browser (CwSendPanel sequences
    // the KM/KY writes), so this page needs only the speed and break-in the
    // radio is set to when it opens; changes after that arrive over SignalR.
    public class CwSendModel : PageModel
    {
        private readonly RadioStateService _radioState;

        public CwSendModel(RadioStateService radioState)
        {
            _radioState = radioState;
        }

        public int CwSpeed => _radioState.CwSpeed;
        public string CwBreakIn => _radioState.CwBreakIn ?? "";
        // The panel warns when the transmit VFO is not in CW (KY keys nothing then).
        public string ModeA => _radioState.ModeA ?? "";
        public string ModeB => _radioState.ModeB ?? "";
        public int TxVfo => _radioState.TxVfo;

        public void OnGet()
        {
        }
    }
}
