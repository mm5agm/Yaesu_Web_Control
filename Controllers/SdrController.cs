// Yaesu Web Control – SDR REST Controller
// Provides device enumeration for the Settings page dropdown.
// No UI logic, no SignalR, no calibration.
//
// Response shape: { devices: [...], notes: ["..."] }
// 'notes' contains plain-English installation guidance when drivers are missing.

using Yaesu_Web_Control.Services.Sdr;
using Microsoft.AspNetCore.Mvc;

namespace Yaesu_Web_Control.Controllers
{
    [ApiController]
    [Route("api/sdr")]
    public class SdrController : ControllerBase
    {
        private readonly ILogger<SdrController> _logger;
        private readonly SdrManager             _sdrManager;

        public SdrController(ILogger<SdrController> logger, SdrManager sdrManager)
        {
            _logger     = logger;
            _sdrManager = sdrManager;
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
    }
}
