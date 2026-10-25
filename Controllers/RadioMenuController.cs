using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;
using Yaesu_Web_Control.Services.RadioMenu;

namespace Yaesu_Web_Control.Controllers
{
    /// <summary>
    /// Saves the radio's own menu settings to a file, like the radio's
    /// MENU SAVE to SD card but readable in Notepad. Read-only: every
    /// item is fetched with an EX read and nothing is written to the radio.
    /// </summary>
    [ApiController]
    [Route("api/radiomenu")]
    public class RadioMenuController : ControllerBase
    {
        private const string ClientId = "RadioMenu";
        private const int TimeoutMs = 300;
        private const int Attempts = 3;

        private readonly ICatClient _cat;
        private readonly ISettingsService _settings;
        private readonly ILogger<RadioMenuController> _logger;

        public RadioMenuController(ICatClient cat, ISettingsService settings, ILogger<RadioMenuController> logger)
        {
            _cat = cat;
            _settings = settings;
            _logger = logger;
        }

        /// <summary>
        /// GET /api/radiomenu/export — reads every menu item and returns an .ini file.
        /// About 200 reads on an FTdx101, a few seconds at 38400 bps.
        /// </summary>
        [HttpGet("export")]
        public async Task<IActionResult> Export(CancellationToken ct)
        {
            var model = _settings.GetCachedSettings().RadioModel;
            var table = RadioMenuCat.TableFor(model);
            if (table is null)
                return BadRequest(new { error = $"Saving the radio menu is not available for the {model} yet." });
            if (!_cat.IsConnected)
                return BadRequest(new { error = "The radio is not connected." });

            var readings = new List<RadioMenuReading>(table.Count);
            foreach (var item in table)
                readings.Add(new RadioMenuReading(item, await ReadAsync(item.Address, ct)));

            int missing = readings.Count(r => r.Raw is null);
            _logger.LogInformation("Radio menu save: {Read} of {Total} items read from the {Model}",
                readings.Count - missing, readings.Count, model);
            if (missing == readings.Count)
                return StatusCode(504, new { error = "The radio did not answer any menu reads." });

            var now = DateTime.Now;
            var ini = RadioMenuCat.ToIni(model, now, readings);
            var name = $"YWC radio menu {model} {now:yyyy-MM-dd HHmm}.ini";
            Response.Headers["X-Menu-Missing"] = missing.ToString();
            // UTF-8 with BOM so older Notepad opens it as UTF-8.
            var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(ini)).ToArray();
            return File(bytes, "text/plain", name);
        }

        private async Task<string?> ReadAsync(string address, CancellationToken ct)
        {
            var command = RadioMenuCat.ReadCommand(address);
            for (int i = 0; i < Attempts; i++)
            {
                var value = RadioMenuCat.ParseAnswer(address,
                    await _cat.SendCommandAsync(command, ClientId, ct, TimeoutMs));
                if (value is not null) return value;
            }
            _logger.LogWarning("Radio menu save: no answer for EX{Address}", address);
            return null;
        }
    }
}
