using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;
using Yaesu_Web_Control.Services.Audio;

namespace Yaesu_Web_Control.Controllers
{
    /// <summary>
    /// Backs the "set this up now" pop-outs on the Home page.
    ///
    /// A panel on Home can be visible and offering a feature that cannot
    /// actually work until something is chosen in Settings — the CW reader
    /// needs a radio RX capture device, the DX spots panel needs a cluster
    /// host and a login callsign. Rather than send the operator off to
    /// Settings to hunt for the right field, each panel offers to fix its own
    /// prerequisite in place, and this controller is what those pop-outs
    /// write through.
    ///
    /// It is deliberately NOT a general settings-write endpoint. Each action
    /// touches a named, hard-coded set of fields and nothing else, so a bug
    /// or a stray request here can never rewrite unrelated configuration.
    /// Anything outside that whitelist belongs on the Settings page.
    /// </summary>
    [ApiController]
    [Route("api/feature-setup")]
    public class FeatureSetupController : ControllerBase
    {
        private readonly ISettingsService _settings;
        private readonly RadioAudioBridgeService _bridge;
        private readonly ILogger<FeatureSetupController> _logger;

        public FeatureSetupController(
            ISettingsService settings,
            RadioAudioBridgeService bridge,
            ILogger<FeatureSetupController> logger)
        {
            _settings = settings;
            _bridge = bridge;
            _logger = logger;
        }

        // ── Current state ──────────────────────────────────────────────────
        // What the pop-outs pre-fill their fields from. Only the fields the
        // pop-outs can write are returned.

        [HttpGet("state")]
        public async Task<IActionResult> State()
        {
            var s = await _settings.GetSettingsAsync();
            return Ok(new
            {
                dxCluster = new
                {
                    enabled = s.DxClusterEnabled,
                    host = s.DxClusterHost ?? "",
                    port = s.DxClusterPort,
                    callsign = s.DxClusterLoginCallsign ?? ""
                },
                cwAudio = new
                {
                    rxDevice = s.AudioRadioRxDevice ?? "",
                    devicesOpen = _bridge.DevicesOpen
                }
            });
        }

        // ── DX cluster ─────────────────────────────────────────────────────
        // Writes DxClusterEnabled / Host / Port / LoginCallsign and nothing
        // else. DxClusterService re-reads settings every 15 s while idle, so
        // no restart is needed — the caller can honestly say "within about
        // 15 seconds".

        public class DxClusterRequest
        {
            public bool Enabled { get; set; } = true;
            public string Host { get; set; } = "";
            public int Port { get; set; } = 7300;
            public string Callsign { get; set; } = "";
        }

        [HttpPost("dx-cluster")]
        public async Task<IActionResult> SaveDxCluster([FromBody] DxClusterRequest? req)
        {
            req ??= new DxClusterRequest();
            var host = (req.Host ?? "").Trim();
            var callsign = (req.Callsign ?? "").Trim().ToUpperInvariant();

            if (req.Enabled)
            {
                if (host.Length == 0)
                    return Ok(new { success = false, error = "Enter a cluster host, for example gb7ujs.ddns.net." });
                if (callsign.Length == 0)
                    return Ok(new { success = false, error = "Enter your callsign — the cluster uses it to log you in." });
                if (req.Port <= 0 || req.Port > 65535)
                    return Ok(new { success = false, error = $"Port {req.Port} is not a valid TCP port." });
            }

            var s = await _settings.GetSettingsAsync();
            s.DxClusterEnabled = req.Enabled;
            s.DxClusterHost = host;
            s.DxClusterPort = req.Port <= 0 || req.Port > 65535 ? 7300 : req.Port;
            s.DxClusterLoginCallsign = callsign;
            await _settings.SaveSettingsAsync(s);

            _logger.LogInformation("[FeatureSetup] DX cluster set to {Host}:{Port} as {Call} (enabled={Enabled})",
                host, s.DxClusterPort, callsign, req.Enabled);

            return Ok(new
            {
                success = true,
                message = req.Enabled
                    ? "Saved. The cluster connects within about 15 seconds."
                    : "Saved. The DX cluster is now off."
            });
        }

        // ── CW reader audio ────────────────────────────────────────────────
        // Writes AudioRadioRxDevice and nothing else. The CW reader takes a
        // capture-only hold on the radio's RX endpoint, so the TX device is
        // none of this pop-out's business — leaving it alone also means the
        // pop-out cannot break an operator's Remote Audio setup.

        public class AudioRxRequest
        {
            public string Device { get; set; } = "";
        }

        [HttpPost("audio-rx")]
        public async Task<IActionResult> SaveAudioRx([FromBody] AudioRxRequest? req)
        {
            var device = (req?.Device ?? "").Trim();
            if (device.Length == 0)
                return Ok(new { success = false, error = "Choose the radio's USB recording device." });

            var s = await _settings.GetSettingsAsync();
            s.AudioRadioRxDevice = device;
            await _settings.SaveSettingsAsync(s);

            _logger.LogInformation("[FeatureSetup] Radio RX capture device set to {Device}", device);

            return Ok(new { success = true, message = "Saved. Start the CW reader again." });
        }
    }
}
