using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;
using Yaesu_Web_Control.Services.Audio;

namespace Yaesu_Web_Control.Controllers
{
    [ApiController]
    [Route("api/audio")]
    public class AudioController : ControllerBase
    {
        private readonly ISettingsService _settings;
        private readonly AudioSessionManager _sessions;
        private readonly RadioAudioBridgeService _bridge;
        private readonly ILogger<AudioController> _logger;

        public AudioController(
            ISettingsService settings,
            AudioSessionManager sessions,
            RadioAudioBridgeService bridge,
            ILogger<AudioController> logger)
        {
            _settings = settings;
            _sessions = sessions;
            _bridge = bridge;
            _logger = logger;
        }

        [HttpGet("devices")]
        public IActionResult ListDevices()
        {
            try
            {
                var inputs = AudioDeviceEnumerator.ListInputs()
                    .Select(d => new
                    {
                        d.Index,
                        d.Name,
                        d.HostApiName,
                        d.HostApiIndex,
                        displayName = d.DisplayName,
                        key = d.PersistenceKey,
                        d.DefaultSampleRate
                    });
                var outputs = AudioDeviceEnumerator.ListOutputs()
                    .Select(d => new
                    {
                        d.Index,
                        d.Name,
                        d.HostApiName,
                        d.HostApiIndex,
                        displayName = d.DisplayName,
                        key = d.PersistenceKey,
                        d.DefaultSampleRate
                    });
                return Ok(new { inputs, outputs });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enumerate audio devices");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("status")]
        public async Task<IActionResult> Status()
        {
            var s = await _settings.GetSettingsAsync();
            return Ok(new
            {
                enabled = s.AudioStreamingEnabled,
                sessionActive = _sessions.HasActiveSession,
                devicesOpen = _bridge.DevicesOpen,
                codec = _bridge.ActiveCodec,
                rxLevel = _bridge.RxLevel,
                txLevel = _bridge.TxLevel,
                rxGain = s.AudioRxGain,
                txGain = s.AudioTxGain,
                httpsEnabled = s.HttpsEnabled,
                httpsPort = s.HttpsPort,
                certificatePresent = HttpsCertificateService.CertificateExists,
                certificateInfo = HttpsCertificateService.DescribeCertificate()
            });
        }

        public sealed class GainRequest
        {
            public float? Rx { get; set; }
            public float? Tx { get; set; }
        }

        /// <summary>Persist and apply RX/TX software gains (0.05–4).</summary>
        [HttpPost("gain")]
        public async Task<IActionResult> SetGain([FromBody] GainRequest? body)
        {
            var s = await _settings.GetSettingsAsync();
            if (body?.Rx is float rx) s.AudioRxGain = Math.Clamp(rx, 0.05f, 4f);
            if (body?.Tx is float tx) s.AudioTxGain = Math.Clamp(tx, 0.05f, 4f);
            await _settings.SaveSettingsAsync(s);
            _bridge.SetGains(s.AudioRxGain, s.AudioTxGain);
            return Ok(new { rx = s.AudioRxGain, tx = s.AudioTxGain });
        }

        public sealed class GenerateCertRequest
        {
            public string? SanHosts { get; set; }
        }

        [HttpPost("https/generate")]
        public IActionResult GenerateCertificate([FromBody] GenerateCertRequest? body)
        {
            try
            {
                var hosts = (body?.SanHosts ?? "")
                    .Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                HttpsCertificateService.Generate(hosts);
                _logger.LogInformation("Generated self-signed HTTPS certificate at {Path}", HttpsCertificateService.CertificatePath);
                return Ok(new
                {
                    ok = true,
                    path = HttpsCertificateService.CertificatePath,
                    info = HttpsCertificateService.DescribeCertificate(),
                    restartRequired = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HTTPS certificate generation failed");
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}
