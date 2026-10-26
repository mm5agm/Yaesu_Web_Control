using Microsoft.AspNetCore.Mvc;
using RadioWebControl.Core.Services.Flex;

namespace Yaesu_Web_Control.Controllers
{
    /// <summary>
    /// Named layouts for the RTTY Contest view (/rtty-contest). The API is
    /// Experimental's (<see cref="LayoutStoreController"/>), so there is one
    /// piece of code to keep; only the route and the store's file differ.
    /// </summary>
    [Route("api/rttycontestlayouts")]
    public class RttyContestLayoutController : LayoutStoreController
    {
        public const string StoreKey = "rtty-contest";

        public RttyContestLayoutController([FromKeyedServices(StoreKey)] IFlexLayoutStore store)
            : base(store)
        {
        }
    }
}
