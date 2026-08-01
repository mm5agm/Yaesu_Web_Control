// Yaesu Web Control – SDR REST Controller
// Provides device enumeration for the Settings page dropdown.
// No UI logic, no SignalR, no calibration.
//
// Response shape: { devices: [...], notes: ["..."] }
// 'notes' contains plain-English installation guidance when drivers are missing.

using Yaesu_Web_Control.Services.Sdr;
using Yaesu_Web_Control.Services;
using Microsoft.AspNetCore.Mvc;

namespace Yaesu_Web_Control.Controllers
{
    [ApiController]
    [Route("api/sdr")]
    public class SdrController : ControllerBase
    {
        private readonly ILogger<SdrController> _logger;
        private readonly SdrManager             _sdrManager;
        private readonly ISettingsService       _settings;

        public SdrController(ILogger<SdrController> logger, SdrManager sdrManager, ISettingsService settings)
        {
            _logger     = logger;
            _sdrManager = sdrManager;
            _settings   = settings;
        }

        /// <summary>
        /// Returns connected SDR devices plus plain-English installation notes.
        /// Devices currently held by running workers get `inUse=true` so the
        /// Settings page can label them as "in use" rather than misleadingly
        /// reporting them as missing — the SDRplay API hides Selected devices
        /// from subsequent GetDevices calls, so this controller can't see
        /// them through normal enumeration.
        /// Always responds 200.
        /// </summary>
        [HttpGet("devices")]
        public IActionResult GetDevices()
        {
            var all   = new List<SdrDeviceInfo>();
            // Notes are collected separately as deferred-add candidates and only
            // attached to the final response if `all` is still empty after we've
            // merged in worker-held devices. Otherwise we'd report 'No SDR
            // devices detected' alongside a populated dropdown — which is what
            // users saw on v2.3.1/v2.3.2 when their SDRs were actively
            // streaming via workers (the direct enumeration legitimately
            // returns 0 because the API hides Selected devices, but the
            // dropdown ends up populated by the worker merge below).
            var pendingNotes = new List<string>();
            var notes        = new List<string>();

            // ── SDRplay (sdrplay_api.dll) ────────────────────────────────────────
            var sdrplay = SdrplayDevice.EnumerateDevices(out string? sdrplayNote);
            if (sdrplay.Count > 0)
            {
                all.AddRange(sdrplay);
                _logger.LogDebug("SDR: Found {Count} SDRplay device(s)", sdrplay.Count);
            }
            else if (sdrplayNote != null)
            {
                pendingNotes.Add(sdrplayNote);
                _logger.LogWarning("SDR: SDRplay — {Note}", sdrplayNote);
            }

            // ── SoapySDR (SoapySDR.dll) ──────────────────────────────────────────
            try
            {
                var soapy = SoapySdrInterop.EnumerateDevices();

                // If the direct SDRplay path already found devices, suppress SoapySDR's
                // sdrplay-driver entries — they are the same physical hardware via an
                // inferior code path and would show as duplicates in the dropdown.
                var soapyFiltered = sdrplay.Count > 0
                    ? soapy.Where(d => !d.Driver.Equals("sdrplay", StringComparison.OrdinalIgnoreCase)).ToList()
                    : soapy;

                if (soapyFiltered.Count > 0)
                {
                    all.AddRange(soapyFiltered);
                    _logger.LogDebug("SDR: Found {Count} SoapySDR device(s)", soapyFiltered.Count);
                }
                else if (soapy.Count == 0 && all.Count == 0)
                {
                    // No devices found via any path — diag deferred to the
                    // post-worker-merge gate below.
                    string diag = SoapySdrInterop.GetPluginDiagnostics();
                    pendingNotes.Add("No SDR devices detected. " +
                                     "Plugin details: | " + diag.Replace("\n", " | "));
                    _logger.LogWarning("SDR: SoapySDR no devices. {Diag}", diag);
                }
            }
            catch (DllNotFoundException ex)
            {
                bool missingDependency = ex.Message.Contains("dependencies",
                    StringComparison.OrdinalIgnoreCase);

                // SoapySDR.dll itself is missing — a different problem from
                // "the DLL is fine but no devices are plugged in". This note
                // is always relevant to the user regardless of what workers
                // are doing, so it goes straight to the response.
                notes.Add(missingDependency
                    ? "SoapySDR.dll loaded but a dependency it needs is missing. " +
                      "Try re-installing the application — the installer bundles all required DLLs."
                    : "SoapySDR.dll not found. Try re-installing the application — the installer " +
                      "should have placed SoapySDR\\bin\\SoapySDR.dll in the application folder.");
                _logger.LogWarning("SDR: SoapySDR DllNotFoundException — {Msg}", ex.Message);
            }
            catch (Exception ex)
            {
                notes.Add($"SoapySDR error: {ex.Message}");
                _logger.LogWarning(ex, "SDR: SoapySDR enumeration failed");
            }

            // Merge in devices currently held by running workers. The SDRplay
            // API hides Selected devices from GetDevices in other processes,
            // so without this the Settings Scan would report the user's
            // already-configured device as "not found" while it's actively
            // streaming — confusing.
            var enumeratedKeys = new HashSet<string>(all.Select(d => d.Key), StringComparer.OrdinalIgnoreCase);
            var activeByVfo    = _sdrManager.GetActiveDeviceKeys();   // vfo → key
            var inUseKeys      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (vfo, key) in activeByVfo)
            {
                inUseKeys.Add(key);
                if (enumeratedKeys.Contains(key)) continue;   // already in the list

                // Build a synthetic entry from the key alone.
                string label, driver;
                if (key.StartsWith(SdrplayDevice.KeyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    label  = SdrplayDevice.LabelForKey(key);
                    driver = "sdrplay";
                }
                else
                {
                    // SoapySDR kwargs string — use it directly as label.
                    label  = key;
                    driver = "soapy";
                }
                all.Add(new SdrDeviceInfo(key, label, driver));
                _logger.LogDebug("SDR: surfaced active worker device {Key} (VFO {Vfo}) — direct enumeration hid it", key, vfo);
            }

            // Only surface the "no SDRplay devices found" / "no devices detected"
            // notes if the device list is STILL empty after the worker-held merge.
            // If workers are holding devices, those messages are misleading —
            // direct enumeration found nothing because of the API's Selected-device
            // hiding behaviour, not because the user actually has no SDRs.
            if (all.Count == 0)
            {
                notes.AddRange(pendingNotes);
            }

            return Ok(new
            {
                devices = all.Select(d => new
                {
                    d.Key,
                    d.Label,
                    d.Driver,
                    inUse = inUseKeys.Contains(d.Key),
                }),
                notes
            });
        }

        // ── SDRplay install path helpers (for the Settings page) ────────────
        //
        // GET  /api/sdr/sdrplay/detected-installdir
        //   Returns { detected: "..." | null } — the path SdrplayDllResolver
        //   would resolve to right now, useful for the Settings page to show
        //   "Auto-detected: <path>" alongside the user-override input.
        //
        // POST /api/sdr/sdrplay/pick-installdir  body { startPath: "..." }
        //   Opens a native Windows FolderBrowserDialog on the YWC host so
        //   the user can navigate to the SDRplay install folder. Returns
        //   { picked: "..." | null } where null means the user cancelled.
        //   The dialog runs on a temporary STA thread; the request thread
        //   awaits the user's selection.

        [HttpGet("sdrplay/detected-installdir")]
        public IActionResult DetectedSdrplayInstallDir()
        {
            return Ok(new { detected = SdrplayDllResolver.DetectInstallDir() });
        }

        public class PickInstallDirRequest
        {
            public string? StartPath { get; set; }
        }

        [HttpPost("sdrplay/pick-installdir")]
        public async Task<IActionResult> PickSdrplayInstallDir([FromBody] PickInstallDirRequest? request)
        {
            // The folder picker is a native dialog on the YWC host's desktop.
            // If the request came from a remote IP, the user can't see the
            // dialog so the picker would appear to hang. Refuse with a clear
            // error so the remote user knows to come to the host machine.
            var remote = HttpContext.Connection.RemoteIpAddress;
            if (remote != null && !System.Net.IPAddress.IsLoopback(remote))
            {
                return BadRequest(new {
                    error = "The folder browser opens on the YWC host machine's desktop, so it only works when the browser is running on the same PC as YWC. Either open YWC's Settings page in a browser on the host machine to use Browse…, or type the path manually into the field on this remote browser."
                });
            }

            string start = request?.StartPath ?? SdrplayDllResolver.DetectInstallDir() ?? @"C:\Program Files\SDRplay\API";
            try
            {
                var picked = await PickFolderAsync(
                    description: "Select the SDRplay API install folder (the one containing the x64 subfolder)",
                    startPath:   start);
                return Ok(new { picked });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Native folder picker failed");
                return StatusCode(500, new { error = "Folder picker failed: " + ex.Message });
            }
        }

        // Shows System.Windows.Forms.FolderBrowserDialog on a fresh STA
        // thread (HTTP request threads aren't STA). The dialog opens on
        // the YWC host machine's desktop — appropriate when the user is
        // running YWC at http://localhost:8080 (the common case).
        private static Task<string?> PickFolderAsync(string description, string startPath)
        {
            var tcs = new TaskCompletionSource<string?>();
            var thread = new Thread(() =>
            {
                try
                {
#pragma warning disable CA1416 // Validate platform compatibility (project is win-only)
                    using var dlg = new System.Windows.Forms.FolderBrowserDialog
                    {
                        Description           = description,
                        UseDescriptionForTitle = true,
                        ShowNewFolderButton   = false,
                        SelectedPath          = Directory.Exists(startPath) ? startPath : "",
                    };
                    var result = dlg.ShowDialog();
                    tcs.SetResult(result == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null);
#pragma warning restore CA1416
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return tcs.Task;
        }

        // ── Spectrum DSP knobs (Gain only, since the auto-floor port) ───────
        //
        // POST /api/sdr/dsp/{vfo}  body { gainLinear }
        //
        // Since the client-side auto-floor port (see wwwroot/js/sdr/spectrum-panel.js),
        // the trace's vertical scaling is done entirely in the browser — the worker's
        // dB clamp window is FORCED WIDE ([WideDbFloor, WideDbCeiling]) so the client
        // always receives the full dynamic range to auto-range against. The only
        // per-VFO knob left here is GainLinear (waterfall brightness). We still write
        // the wide window into Low/High settings so existing users' stale narrow
        // Low/High values auto-migrate on the first Gain change (and SdrManager pushes
        // the same wide window on worker spawn).
        //
        // Persists to appsettings.user.json AND pushes live to the worker holding
        // {vfo}. Returns { applied = true|false } — applied=false means the persist
        // succeeded but no worker was connected to push to.

        // Fixed wide clamp window — the client auto-floor windows within this.
        private const float WideDbFloor   = -160f;
        private const float WideDbCeiling = 0f;

        public class DspSettingsRequest
        {
            public float GainLinear { get; set; } = 1.0f;
            // DbFloor/DbCeiling are accepted for backward compatibility but ignored:
            // the worker window is forced wide (see WideDbFloor/WideDbCeiling).
            public float DbFloor    { get; set; } = WideDbFloor;
            public float DbCeiling  { get; set; } = WideDbCeiling;
        }

        [HttpPost("dsp/{vfo}")]
        public async Task<IActionResult> SetDspSettings(string vfo, [FromBody] DspSettingsRequest req)
        {
            string v = (vfo ?? "").Trim().ToUpperInvariant();
            if (v != "A" && v != "B") return BadRequest(new { error = "vfo must be 'a' or 'b'" });
            if (req == null)          return BadRequest(new { error = "missing body" });
            if (!float.IsFinite(req.GainLinear) || req.GainLinear <= 0)
                return BadRequest(new { error = "gainLinear must be > 0" });

            // Persist to settings (round-trip read-modify-write). Force the wide
            // clamp window so any stale narrow Low/High is migrated away.
            var settings = await _settings.GetSettingsAsync();
            if (v == "B")
            {
                settings.SdrSpectrumGainB   = req.GainLinear;
                settings.SdrSpectrumLowDbB  = WideDbFloor;
                settings.SdrSpectrumHighDbB = WideDbCeiling;
            }
            else
            {
                settings.SdrSpectrumGainA   = req.GainLinear;
                settings.SdrSpectrumLowDbA  = WideDbFloor;
                settings.SdrSpectrumHighDbA = WideDbCeiling;
            }
            await _settings.SaveSettingsAsync(settings);

            // Push live to the worker if one is connected for this VFO.
            bool applied = await _sdrManager.TrySendDspSettingsAsync(
                v, new DspSettingsPayload(req.GainLinear, WideDbFloor, WideDbCeiling));

            return Ok(new { applied });
        }
    }
}
