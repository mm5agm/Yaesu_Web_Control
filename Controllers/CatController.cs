using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;
using Yaesu_Web_Control.Models;
using System.Text.Json;
using System.Threading;

namespace Yaesu_Web_Control.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CatController : ControllerBase
    {
        private readonly ICatClient _catClient;
        private readonly ISettingsService _settingsService;
        private readonly ILogger<CatController> _logger;
        private readonly RadioStateService _radioStateService;
        private readonly RadioStatePersistenceService _statePersistence;
        private readonly RadioInitializationService _radioInitService;
        private readonly AudioFilterMapService _audioFilterMap;
        private static readonly SemaphoreSlim _requestSemaphore = new(1, 1);

        // -- P1=0-Fixed outgoing-command helpers -------------------------------
        //
        // On single-receiver radios (FTdx10 / FT-710 / FTDX3000 / FT-991A)
        // every P1=0-Fixed receive-control CAT command (GT, PA, RA, NR, NB,
        // NL, BC, BP, CO, SH, IS, SL, RL, AG, RG, SQ) must use P1=0 -- the
        // radio's firmware hard-codes that position to 0, and silently
        // rejects commands sent with P1=1 (which is what YWC was doing when
        // the user clicked a control on panel B). On dual-receiver (FTdx101)
        // P1 genuinely addresses MAIN vs SUB.
        //
        // SP3L Jacek #34 pre7: this is the outbound match for the inbound
        // dispatcher fix we did in pre5/pre6 (SetPerVfo). Without it, Jacek
        // saw "VFO-B active, Contour switching does not work, IF width does
        // not work" -- because CO1... and SH1... were being sent and the
        // FTdx10 was ignoring them.

        /// <summary>
        /// Returns the P1 character for a per-VFO CAT command, given the
        /// user's clicked receiver ("A" or "B"). Delegates to
        /// <see cref="RadioCapabilities.VfoP1"/> -- see that method for the
        /// single- vs dual-receiver rule.
        /// </summary>
        private string VfoP1Outgoing(string receiver) =>
            RadioCapabilities.VfoP1(_radioStateService.IsSingleReceiver, receiver);

        /// <summary>
        /// Returns true if the per-VFO state write should target *B (vs *A)
        /// for a user-clicked receiver. Delegates to
        /// <see cref="RadioCapabilities.VfoIsB"/>.
        /// </summary>
        private bool VfoIsB(string receiver) =>
            RadioCapabilities.VfoIsB(_radioStateService.IsSingleReceiver, _radioStateService.ActiveVfo, receiver);

        [HttpPost("afgain/a")]
        public async Task<IActionResult> SetAfGainA([FromBody] int value)
        {
            if (value < 0 || value > 255)
                return BadRequest(new { error = "AF Gain value out of range (0-255)" });
            await EnsureConnectedAsync();
            // VfoP1Outgoing("A") = "0" on both single and dual receivers
            await _catClient.SendCommandAsync($"AG{VfoP1Outgoing("A")}{value:D3};", "WebUI", CancellationToken.None);
            if (VfoIsB("A")) _radioStateService.AfGainB = value;
            else             _radioStateService.AfGainA = value;
            return Ok(new { message = $"AF Gain {value} set for Receiver A" });
        }

        [HttpPost("afgain/b")]
        public async Task<IActionResult> SetAfGainB([FromBody] int value)
        {
            if (value < 0 || value > 255)
                return BadRequest(new { error = "AF Gain value out of range (0-255)" });
            await EnsureConnectedAsync();
            // VfoP1Outgoing("B") = "0" on single-receiver (radio rejects AG1...),
            // "1" on dual-receiver (FTdx101 has independent SUB AF gain).
            await _catClient.SendCommandAsync($"AG{VfoP1Outgoing("B")}{value:D3};", "WebUI", CancellationToken.None);
            if (VfoIsB("B")) _radioStateService.AfGainB = value;
            else             _radioStateService.AfGainA = value;
            return Ok(new { message = $"AF Gain {value} set for Receiver B" });
        }

        [HttpPost("micgain")]
        public async Task<IActionResult> SetMicGain([FromBody] MicGainRequest request)
        {
            _logger.LogInformation("[API] SetMicGain called: value={Value}", request.Value);

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();
                if (request.Value < 0 || request.Value > 100)
                    return BadRequest(new { error = "MIC Gain value out of range (0-100)" });

                string command = $"MG{request.Value:D3};";
                await _catClient.SendCommandAsync(command, "WebUI", CancellationToken.None);

                // Persist MIC Gain value
                _logger.LogWarning("[MicGain API] Setting _radioStateService.MicGain to {Value}", request.Value);
                _radioStateService.MicGain = request.Value;

                _logger.LogInformation("Set MIC Gain to {Value}", request.Value);
                return Ok(new { message = $"MIC Gain set to {request.Value}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting MIC Gain");
                return StatusCode(500, new { error = "Failed to set MIC Gain" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        [HttpPost("proc")]
        public async Task<IActionResult> SetProc([FromBody] ProcRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                // PR set format is "PR P1 P2 ;" where P1=0 selects Speech
                // Processor (P1=1 is Parametric Mic EQ, not what we want), and
                // P2=0=OFF / P2=1=ON. The CAT manual lists P2 as 1=OFF/2=ON
                // but bench testing on the FTdx101MP (2026-06-25) showed the
                // manual is wrong: 0=OFF and 1=ON are the values the radio
                // actually accepts. Sending "PR0;"/"PR1;" (without P2) is a
                // read command, which is why the button used to be a no-op.
                string command = request.Enabled ? "PR01;" : "PR00;";
                await _catClient.SendCommandAsync(command, "WebUI", CancellationToken.None);
                _radioStateService.ProcEnabled = request.Enabled;
                return Ok(new { message = $"PROC {(request.Enabled ? "ON" : "OFF")}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting PROC");
                return StatusCode(500, new { error = "Failed to set PROC" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        [HttpPost("proclevel")]
        public async Task<IActionResult> SetProcLevel([FromBody] ProcLevelRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                if (request.Value < 0 || request.Value > 100)
                    return BadRequest(new { error = "PROC level out of range (0-100)" });
                await _catClient.SendCommandAsync($"PL{request.Value:D3};", "WebUI", CancellationToken.None);

                // Read back to confirm what the radio actually stored.
                // Response format: "PLnnn;" (nnn = 000-100).
                var response = await _catClient.SendCommandAsync("PL;", "WebUI", CancellationToken.None);
                int actualValue = request.Value;
                if (!string.IsNullOrEmpty(response) && response.Length >= 5)
                {
                    var valueStr = response.Substring(2, 3);
                    if (int.TryParse(valueStr, out int parsed) && parsed >= 0 && parsed <= 100)
                        actualValue = parsed;
                }
                _radioStateService.ProcLevel = actualValue;
                if (actualValue != request.Value)
                    _logger.LogWarning("PROC level mismatch: requested {Requested}, radio returned {Actual}", request.Value, actualValue);
                return Ok(new { message = $"PROC level set to {actualValue}", actual = actualValue });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting PROC level");
                return StatusCode(500, new { error = "Failed to set PROC level" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        [HttpPost("radiopower")]
        public async Task<IActionResult> SetRadioPower([FromBody] RadioPowerRequest request)
        {
            _logger.LogInformation("[API] SetRadioPower called: powerOn={PowerOn}", request.PowerOn);
            try
            {
                if (request.PowerOn)
                {
                    _logger.LogInformation("Turning radio ON...");
                    await _catClient.SendCommandAsync("PS1;", "WebUI", CancellationToken.None);
                    await Task.Delay(1500);
                    await _catClient.SendCommandAsync("PS1;", "WebUI", CancellationToken.None);
                    _radioStateService.RadioPowerOn = true;
                    _logger.LogInformation("Radio power ON command sent");
                    await Task.Delay(3000);
                    _logger.LogInformation("Re-initializing radio after power on...");
                    await _radioInitService.InitializeRadioAsync();
                    _logger.LogInformation("[API] SetRadioPower completed: powerOn=true");
                    return Ok(new { message = "Radio powered ON and initialized", powerOn = true });
                }
                else
                {
                    _logger.LogInformation("Turning radio OFF...");
                    await _catClient.SendCommandAsync("PS0;", "WebUI", CancellationToken.None);
                    _radioStateService.RadioPowerOn = false;
                    AppStatus.InitializationStatus = "radio_off";
                    _logger.LogInformation("Radio power OFF command sent");
                    _logger.LogInformation("[API] SetRadioPower completed: powerOn=false");
                    return Ok(new { message = "Radio powered OFF", powerOn = false });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting radio power");
                return StatusCode(500, new { error = "Failed to set radio power" });
            }
        }

        [HttpGet("radiopower")]
        public IActionResult GetRadioPowerStatus()
        {
            return Ok(new { powerOn = _radioStateService.RadioPowerOn });
        }

        [HttpPost("tx")]
        public async Task<IActionResult> ToggleTransmit([FromBody] TxRequest request)
        {
            _logger.LogInformation("[API] ToggleTransmit called: transmit={Transmit}", request.Transmit);
            try
            {
                if (request.Transmit)
                {
                    _logger.LogInformation("Turning TX ON...");
                    await _catClient.SendCommandAsync("TX1;", "WebUI", CancellationToken.None);
                    _radioStateService.IsTransmitting = true;
                    _logger.LogInformation("[API] ToggleTransmit completed: transmitting=true");
                    return Ok(new { message = "TX ON", transmitting = true });
                }
                else
                {
                    _logger.LogInformation("Turning TX OFF...");
                    await _catClient.SendCommandAsync("TX0;", "WebUI", CancellationToken.None);
                    _radioStateService.IsTransmitting = false;
                    _logger.LogInformation("[API] ToggleTransmit completed: transmitting=false");
                    return Ok(new { message = "TX OFF", transmitting = false });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error toggling TX");
                return StatusCode(500, new { error = "Failed to toggle TX" });
            }
        }

        [HttpGet("tx")]
        public IActionResult GetTxStatus()
        {
            return Ok(new { 
                transmitting = _radioStateService.IsTransmitting,
                txVfo = _radioStateService.TxVfo
            });
        }

        // Static band frequency mapping (apply this at the top of your class)
        private static readonly Dictionary<string, long> BandFreqs = new(StringComparer.OrdinalIgnoreCase)
        {
            { "160m", 1840000 }, { "80m", 3700000 }, { "60m", 5357000 },
            { "40m", 7100000 }, { "30m", 10136000 }, { "20m", 14074000 },
            { "17m", 18110000 }, { "15m", 21074000 }, { "12m", 24915000 },
            { "10m", 28074000 }, { "6m", 50313000 }, { "4m", 70100000 }
        };

        private static readonly Dictionary<string, string> CatCodeToMode = new()
        {
            { "1", "LSB" },
            { "2", "USB" },
            { "3", "CW-U" },
            { "4", "FM" },
            { "5", "AM" },
            { "6", "RTTY-L" },
            { "7", "CW-L" },
            { "8", "DATA-L" },
            { "9", "RTTY-U" },
            { "A", "DATA-FM" },
            { "B", "FM-N" },
            { "C", "DATA-U" },
            { "D", "AM-N" },
            { "E", "PSK" },
            { "F", "DATA-FM-N" }
        };

        public CatController(
            ICatClient catClient,
            ISettingsService settingsService,
            ILogger<CatController> logger,
            RadioStateService radioStateService,
            RadioStatePersistenceService statePersistence,
            RadioInitializationService radioInitService,
            AudioFilterMapService audioFilterMap)
        {
            _catClient = catClient;
            _settingsService = settingsService;
            _logger = logger;
            _radioStateService = radioStateService;
            _statePersistence = statePersistence;
            _radioInitService = radioInitService;
            _audioFilterMap = audioFilterMap;
        }

        private async Task EnsureConnectedAsync()
        {
            // RadioInitializationService handles connection and state restoration on startup.
            // This method only needs to verify the connection is still active.
            if (!_catClient.IsConnected)
            {
                var settings = await _settingsService.GetSettingsAsync();
                await _catClient.ConnectAsync(settings.SerialPort, settings.BaudRate);
            }
            // No redundant restoration needed - RadioInitializationService already did it
        }

        private async Task<string> GetMainVfoAsync()
        {
            var response = await _catClient.SendCommandAsync("IF;", "WebUI", CancellationToken.None);
            if (!string.IsNullOrEmpty(response) && response.Length > 5)
                return response[5] == '1' ? "B" : "A";
            return "A";
        }

        [HttpGet("status")]
        public async Task<IActionResult> GetStatus()
        {
            if (_radioStateService.FrequencyA < 100 || _radioStateService.FrequencyB < 100)
            {
                await EnsureConnectedAsync();
            }

            // Log what we're returning for debugging
            _logger.LogInformation("[API] GetStatus called");
            _logger.LogInformation("[API Status] Returning: FreqA={FreqA}, BandA={BandA}, FreqB={FreqB}, BandB={BandB}",
                _radioStateService.FrequencyA, _radioStateService.BandA,
                _radioStateService.FrequencyB, _radioStateService.BandB);

            var settings = await _settingsService.GetSettingsAsync();
            return Ok(new
            {
                isConnected = _radioStateService.IsConnected,
                radioModel = settings.RadioModel,
                vfoA = new
                {
                    frequency = _radioStateService.FrequencyA,
                    band = _radioStateService.BandA,
                    sMeter = _radioStateService.SMeterA ?? 0,
                    power = _radioStateService.PowerMeter ?? 0,
                    mode = _radioStateService.ModeA ?? "",
                    antenna = _radioStateService.AntennaA ?? "",
                    afGain = _radioStateService.AfGainA,
                    roofingFilter = _radioStateService.RoofingFilterA ?? "",
                    ifWidth = _radioStateService.IfWidthA ?? "",
                    ifShift = _radioStateService.IfShiftA
                },
                vfoB = new
                {
                    frequency = _radioStateService.FrequencyB,
                    band = _radioStateService.BandB,
                    sMeter = _radioStateService.SMeterB ?? 0,
                    mode = _radioStateService.ModeB ?? "",
                    antenna = _radioStateService.AntennaB ?? "",
                    afGain = _radioStateService.AfGainB,
                    roofingFilter = _radioStateService.RoofingFilterB ?? "",
                    ifWidth = _radioStateService.IfWidthB ?? "",
                    ifShift = _radioStateService.IfShiftB
                },
                micGain = _radioStateService.MicGain,
                powerMeter = _radioStateService.PowerMeter ?? 0,
                compressionMeter = _radioStateService.CompressionMeter ?? 0,
                swrMeter = _radioStateService.SWRMeter ?? 0,
                alcMeter = _radioStateService.ALCMeter ?? 0,
                iddMeter = _radioStateService.IDDMeter ?? 0,
                vddMeter = _radioStateService.VDDMeter ?? 0,
                temperature = _radioStateService.Temperature ?? 0
            });
        }

        [HttpGet("status/init")]
        public IActionResult GetInitStatus()
        {
            return Ok(new { status = AppStatus.InitializationStatus });
        }

        [HttpPost("frequency/a")]
        public async Task<IActionResult> SetFrequencyA([FromBody] FrequencyRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            _logger.LogInformation("[API] SetFrequencyA called: freq={Freq}", request.FrequencyHz);
            try
            {
                await EnsureConnectedAsync();
                var freq = request.FrequencyHz;
                if (freq < 30000 || freq > 75000000)
                    return BadRequest(new { error = "Frequency out of range" });

                var command = $"FA{freq:D9};";
                await _catClient.SendCommandAsync(command, "WebUI", CancellationToken.None);

                _radioStateService.FrequencyA = freq;

                _logger.LogInformation("Set Receiver A frequency to {Freq}", freq);
                _logger.LogInformation("[API] SetFrequencyA completed: freq={Freq}", freq);
                return Ok(new { message = $"Frequency {freq} Hz set for Receiver A" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Receiver A frequency");
                return StatusCode(500, new { error = "Failed to set frequency" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        [HttpPost("frequency/b")]
        public async Task<IActionResult> SetFrequencyB([FromBody] FrequencyRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            _logger.LogInformation("[API] SetFrequencyB called: freq={Freq}", request.FrequencyHz);
            try
            {
                await EnsureConnectedAsync();
                var freq = request.FrequencyHz;
                if (freq < 30000 || freq > 75000000)
                    return BadRequest(new { error = "Frequency out of range" });

                var command = $"FB{freq:D9};";
                await _catClient.SendCommandAsync(command, "WebUI", CancellationToken.None);

                _radioStateService.FrequencyB = freq;

                _logger.LogInformation("Set Receiver B frequency to {Freq}", freq);
                _logger.LogInformation("[API] SetFrequencyB completed: freq={Freq}", freq);
                return Ok(new { message = $"Frequency {freq} Hz set for Receiver B" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Receiver B frequency");
                return StatusCode(500, new { error = "Failed to set frequency" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        [HttpPost("band/a")]
        public async Task<IActionResult> SetBandA([FromBody] BandRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            _logger.LogInformation("[API] SetBandA called: band={Band}", request.Band);
            try
            {
                await EnsureConnectedAsync();

                if (!BandFreqs.TryGetValue(request.Band, out var freq))
                    return BadRequest(new { error = "Invalid band" });

                var settings = await _settingsService.GetSettingsAsync();

                // Save current band profile before switching
                var oldBand = _radioStateService.BandA;
                if (!string.IsNullOrEmpty(oldBand))
                {
                    settings.BandProfilesA[oldBand] = new BandProfile
                    {
                        IfWidthCode = _radioStateService.IfWidthA,
                        IfShiftHz   = _radioStateService.IfShiftA,
                        Mode        = _radioStateService.ModeA ?? "",
                        Antenna     = _radioStateService.AntennaA ?? ""
                    };
                    await _settingsService.SaveSettingsAsync(settings);
                }

                var command = $"FA{freq:D9};";
                await _catClient.SendCommandAsync(command, "WebUI", CancellationToken.None);

                var actualFreq = await _catClient.QueryFrequencyAAsync("WebUI", CancellationToken.None);
                _radioStateService.SetBand("A", request.Band);
                _radioStateService.FrequencyA = actualFreq;

                // Restore saved profile for the new band if one exists
                if (settings.BandProfilesA.TryGetValue(request.Band, out var profile))
                {
                    if (!string.IsNullOrEmpty(profile.IfWidthCode))
                    {
                        await _catClient.SendCommandAsync($"SH00{int.Parse(profile.IfWidthCode):D2};", "WebUI", CancellationToken.None);
                        _radioStateService.IfWidthA = profile.IfWidthCode;
                    }
                    var sign = profile.IfShiftHz >= 0 ? '+' : '-';
                    await _catClient.SendCommandAsync($"IS00{sign}{Math.Abs(profile.IfShiftHz):D4};", "WebUI", CancellationToken.None);
                    _radioStateService.IfShiftA = profile.IfShiftHz;
                    if (!string.IsNullOrEmpty(profile.Mode))
                    {
                        await _catClient.SendCommandAsync(CatCommands.FormatMode(profile.Mode, false), "WebUI", CancellationToken.None);
                        _radioStateService.ModeA = profile.Mode;
                    }
                    if (!string.IsNullOrEmpty(profile.Antenna))
                    {
                        await _catClient.SendCommandAsync($"AN0{profile.Antenna};", "WebUI", CancellationToken.None);
                        _radioStateService.AntennaA = profile.Antenna;
                    }
                }

                _logger.LogInformation("[API] SetBandA completed: band={Band}, freq={Freq}", request.Band, actualFreq);
                return Ok(new { message = $"Band {request.Band} selected", frequency = actualFreq });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Receiver A band");
                return StatusCode(500, new { error = "Failed to set band" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        [HttpPost("band/b")]
        public async Task<IActionResult> SetBandB([FromBody] BandRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            _logger.LogInformation("[API] SetBandB called: band={Band}", request.Band);
            try
            {
                await EnsureConnectedAsync();

                if (!BandFreqs.TryGetValue(request.Band, out var freq))
                    return BadRequest(new { error = "Invalid band" });

                var settings = await _settingsService.GetSettingsAsync();

                // Save current band profile before switching
                var oldBand = _radioStateService.BandB;
                if (!string.IsNullOrEmpty(oldBand))
                {
                    settings.BandProfilesB[oldBand] = new BandProfile
                    {
                        IfWidthCode = _radioStateService.IfWidthB,
                        IfShiftHz   = _radioStateService.IfShiftB,
                        Mode        = _radioStateService.ModeB ?? "",
                        Antenna     = _radioStateService.AntennaB ?? ""
                    };
                    await _settingsService.SaveSettingsAsync(settings);
                }

                var command = $"FB{freq:D9};";
                await _catClient.SendCommandAsync(command, "WebUI", CancellationToken.None);

                var actualFreq = await _catClient.QueryFrequencyBAsync("WebUI", CancellationToken.None);
                _radioStateService.SetBand("B", request.Band);
                _radioStateService.FrequencyB = actualFreq;

                // Restore saved profile for the new band if one exists
                if (settings.BandProfilesB.TryGetValue(request.Band, out var profile))
                {
                    if (!string.IsNullOrEmpty(profile.IfWidthCode))
                    {
                        await _catClient.SendCommandAsync($"SH10{int.Parse(profile.IfWidthCode):D2};", "WebUI", CancellationToken.None);
                        _radioStateService.IfWidthB = profile.IfWidthCode;
                    }
                    var sign = profile.IfShiftHz >= 0 ? '+' : '-';
                    await _catClient.SendCommandAsync($"IS10{sign}{Math.Abs(profile.IfShiftHz):D4};", "WebUI", CancellationToken.None);
                    _radioStateService.IfShiftB = profile.IfShiftHz;
                    if (!string.IsNullOrEmpty(profile.Mode))
                    {
                        await _catClient.SendCommandAsync(CatCommands.FormatMode(profile.Mode, true), "WebUI", CancellationToken.None);
                        _radioStateService.ModeB = profile.Mode;
                    }
                    if (!string.IsNullOrEmpty(profile.Antenna))
                    {
                        await _catClient.SendCommandAsync($"AN1{profile.Antenna};", "WebUI", CancellationToken.None);
                        _radioStateService.AntennaB = profile.Antenna;
                    }
                }

                _logger.LogInformation("[API] SetBandB completed: band={Band}, freq={Freq}", request.Band, actualFreq);
                return Ok(new { message = $"Band {request.Band} selected", frequency = actualFreq });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Receiver B band");
                return StatusCode(500, new { error = "Failed to set band" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        [HttpPost("antenna/a")]
        public async Task<IActionResult> SetAntennaA([FromBody] AntennaRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
            {
                return StatusCode(503, new { error = "Radio busy" });
            }

            try
            {
                await EnsureConnectedAsync();
                var command = $"AN0{request.Antenna};";
                await _catClient.SendCommandAsync(command, "WebUI", CancellationToken.None);

                _radioStateService.AntennaA = request.Antenna;

                // Persist immediately into the current band's profile.
                // Without this, the antenna selection only lands in
                // settings.BandProfilesA when the user switches AWAY from
                // the band — so a shutdown mid-band would lose the choice.
                var bandA = _radioStateService.BandA;
                if (!string.IsNullOrEmpty(bandA))
                {
                    var settings = await _settingsService.GetSettingsAsync();
                    if (!settings.BandProfilesA.TryGetValue(bandA, out var prof))
                        prof = new BandProfile();
                    prof.Antenna = request.Antenna;
                    settings.BandProfilesA[bandA] = prof;
                    await _settingsService.SaveSettingsAsync(settings);
                }

                _logger.LogInformation("Set Main antenna to {Antenna}", request.Antenna);
                return Ok(new { message = $"Antenna {request.Antenna} selected" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Main antenna");
                return StatusCode(500, new { error = "Failed to set antenna" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        [HttpPost("antenna/b")]
        public async Task<IActionResult> SetAntennaB([FromBody] AntennaRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
            {
                return StatusCode(503, new { error = "Radio busy" });
            }

            try
            {
                await EnsureConnectedAsync();
                var command = $"AN1{request.Antenna};";
                await _catClient.SendCommandAsync(command, "WebUI", CancellationToken.None);

                _radioStateService.AntennaB = request.Antenna;

                // Persist immediately into the current band's profile.
                // See SetAntennaA for the rationale.
                var bandB = _radioStateService.BandB;
                if (!string.IsNullOrEmpty(bandB))
                {
                    var settings = await _settingsService.GetSettingsAsync();
                    if (!settings.BandProfilesB.TryGetValue(bandB, out var prof))
                        prof = new BandProfile();
                    prof.Antenna = request.Antenna;
                    settings.BandProfilesB[bandB] = prof;
                    await _settingsService.SaveSettingsAsync(settings);
                }

                _logger.LogInformation("Set Sub antenna to {Antenna}", request.Antenna);
                return Ok(new { message = $"Antenna {request.Antenna} selected" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Sub antenna");
                return StatusCode(500, new { error = "Failed to set antenna" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        // FTdx101MP/D roofing filter display names (response code -> display name)
        private static readonly Dictionary<string, string> RoofingFilterNames = new()
        {
            { "6", "12 kHz" },
            { "7", "3 kHz" },
            { "8", "1.2 kHz" },
            { "9", "600 Hz" },
            { "A", "300 Hz" }
        };

        // FTdx101MP/D roofing filter set codes (response code -> set code used in RF command)
        private static readonly Dictionary<string, string> RoofingFilterSetCodes = new()
        {
            { "6", "1" },  // 12 kHz
            { "7", "2" },  // 3 kHz
            { "8", "3" },  // 1.2 kHz (option)
            { "9", "4" },  // 600 Hz
            { "A", "5" }   // 300 Hz (option)
        };

        // FTdx10 roofing filter display names (RF read code P3 -> display name)
        private static readonly Dictionary<string, string> FtdxTenRoofingFilterNames = new()
        {
            { "6", "12 kHz" },
            { "7", "3 kHz" },
            { "9", "500 Hz" },
            { "A", "300 Hz" }
        };

        // FTdx10 roofing filter set codes (read code P3 -> set code P2 used in RF command)
        private static readonly Dictionary<string, string> FtdxTenRoofingFilterSetCodes = new()
        {
            { "6", "1" },  // 12 kHz
            { "7", "2" },  // 3 kHz
            { "9", "4" },  // 500 Hz
            { "A", "5" }   // 300 Hz (optional)
        };

        [HttpPost("roofingfilter/a")]
        public async Task<IActionResult> SetRoofingFilterA([FromBody] RoofingFilterRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();

                var settings = await _settingsService.GetSettingsAsync();
                bool isFtdx10  = settings.RadioModel == "FTdx10";
                bool isFt710   = settings.RadioModel == "FT-710";
                bool isFtdx3000 = settings.RadioModel == "FTDX3000";

                if (isFt710)
                    return Ok(new { message = "Roofing filter is selected automatically by the radio" });

                if (isFtdx10)
                    return await SetFtdx10RoofingFilterAsync(request);

                if (isFtdx3000)
                    return await SetFtdx3000RoofingFilterAsync(request);

                // FTdx101MP/D: RF command with set code conversion
                if (!RoofingFilterSetCodes.TryGetValue(request.Filter, out var setCode))
                    return BadRequest(new { error = $"Invalid filter code: {request.Filter}" });

                var rfCommand = $"RF0{setCode};";
                _logger.LogInformation("Sending roofing filter command: {Command}", rfCommand);
                await _catClient.SendCommandAsync(rfCommand, "WebUI", CancellationToken.None);

                await Task.Delay(100);
                var rfReadResponse = await _catClient.SendCommandAsync("RF0;", "WebUI", CancellationToken.None);
                _logger.LogInformation("Read back roofing filter response: {Response}", rfReadResponse);

                if (!string.IsNullOrEmpty(rfReadResponse) && rfReadResponse.Length >= 4)
                {
                    var actualFilter = rfReadResponse[3].ToString();
                    _radioStateService.RoofingFilterA = actualFilter;

                    if (actualFilter != request.Filter)
                    {
                        var requestedName = RoofingFilterNames.GetValueOrDefault(request.Filter, request.Filter);
                        var actualName = RoofingFilterNames.GetValueOrDefault(actualFilter, actualFilter);
                        _logger.LogWarning("Roofing filter {Requested} not available, radio returned {Actual}", requestedName, actualName);
                        return Ok(new { message = $"Filter {requestedName} not installed. Using {actualName}.", warning = true, filter = actualFilter, filterName = actualName });
                    }

                    var filterName = RoofingFilterNames.GetValueOrDefault(actualFilter, actualFilter);
                    _logger.LogInformation("Set Main roofing filter to {Filter}", filterName);
                    return Ok(new { message = $"Roofing filter {filterName} selected", filter = actualFilter, filterName });
                }

                _radioStateService.RoofingFilterA = request.Filter;
                var fallbackName = RoofingFilterNames.GetValueOrDefault(request.Filter, request.Filter);
                return Ok(new { message = $"Roofing filter {fallbackName} selected", filter = request.Filter, filterName = fallbackName });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Main roofing filter");
                return StatusCode(500, new { error = "Failed to set roofing filter" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        [HttpPost("roofingfilter/b")]
        public async Task<IActionResult> SetRoofingFilterB([FromBody] RoofingFilterRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();

                var settings = await _settingsService.GetSettingsAsync();
                bool isFtdx10  = settings.RadioModel == "FTdx10";
                bool isFt710   = settings.RadioModel == "FT-710";
                bool isFtdx3000 = settings.RadioModel == "FTDX3000";

                if (isFt710)
                    return Ok(new { message = "Roofing filter is selected automatically by the radio" });

                if (isFtdx10)
                    return await SetFtdx10RoofingFilterAsync(request);

                if (isFtdx3000)
                    return await SetFtdx3000RoofingFilterAsync(request);

                // FTdx101MP/D: RF command with set code conversion
                if (!RoofingFilterSetCodes.TryGetValue(request.Filter, out var setCode))
                    return BadRequest(new { error = $"Invalid filter code: {request.Filter}" });

                var rfCommand = $"RF1{setCode};";
                _logger.LogInformation("Sending roofing filter command: {Command}", rfCommand);
                await _catClient.SendCommandAsync(rfCommand, "WebUI", CancellationToken.None);

                await Task.Delay(100);
                var rfReadResponse = await _catClient.SendCommandAsync("RF1;", "WebUI", CancellationToken.None);
                _logger.LogInformation("Read back roofing filter response: {Response}", rfReadResponse);

                if (!string.IsNullOrEmpty(rfReadResponse) && rfReadResponse.Length >= 4)
                {
                    var actualFilter = rfReadResponse[3].ToString();
                    _radioStateService.RoofingFilterB = actualFilter;

                    if (actualFilter != request.Filter)
                    {
                        var requestedName = RoofingFilterNames.GetValueOrDefault(request.Filter, request.Filter);
                        var actualName = RoofingFilterNames.GetValueOrDefault(actualFilter, actualFilter);
                        _logger.LogWarning("Roofing filter {Requested} not available, radio returned {Actual}", requestedName, actualName);
                        return Ok(new { message = $"Filter {requestedName} not installed. Using {actualName}.", warning = true, filter = actualFilter, filterName = actualName });
                    }

                    var filterName = RoofingFilterNames.GetValueOrDefault(actualFilter, actualFilter);
                    _logger.LogInformation("Set Sub roofing filter to {Filter}", filterName);
                    return Ok(new { message = $"Roofing filter {filterName} selected", filter = actualFilter, filterName });
                }

                _radioStateService.RoofingFilterB = request.Filter;
                var fallbackName = RoofingFilterNames.GetValueOrDefault(request.Filter, request.Filter);
                return Ok(new { message = $"Roofing filter {fallbackName} selected", filter = request.Filter, filterName = fallbackName });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Sub roofing filter");
                return StatusCode(500, new { error = "Failed to set roofing filter" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        /// <summary>
        /// FTdx10 single-receiver roofing filter: RF0 P2 set / RF0 P3 read.
        /// Per-VFO state is tracked in the active VFO slot (inactive panel is
        /// not editable on single-receiver radios).
        /// </summary>
        private async Task<IActionResult> SetFtdx10RoofingFilterAsync(RoofingFilterRequest request)
        {
            if (!FtdxTenRoofingFilterSetCodes.TryGetValue(request.Filter, out var setCode))
                return BadRequest(new { error = $"Invalid filter code: {request.Filter}" });

            var rfCommand = $"RF0{setCode};";
            _logger.LogInformation("Sending roofing filter command (FTdx10): {Command}", rfCommand);
            await _catClient.SendCommandAsync(rfCommand, "WebUI", CancellationToken.None);

            await Task.Delay(100);
            var rfReadResponse = await _catClient.SendCommandAsync("RF0;", "WebUI", CancellationToken.None);
            _logger.LogInformation("Read back roofing filter response (FTdx10): {Response}", rfReadResponse);

            if (!string.IsNullOrEmpty(rfReadResponse) && rfReadResponse.Length >= 4)
            {
                var actualFilter = rfReadResponse[3].ToString();
                if (_radioStateService.ActiveVfo == 1) _radioStateService.RoofingFilterB = actualFilter;
                else                                   _radioStateService.RoofingFilterA = actualFilter;

                if (actualFilter != request.Filter)
                {
                    var requestedName = FtdxTenRoofingFilterNames.GetValueOrDefault(request.Filter, request.Filter);
                    var actualName = FtdxTenRoofingFilterNames.GetValueOrDefault(actualFilter, actualFilter);
                    _logger.LogWarning("Roofing filter {Requested} not available, radio returned {Actual}", requestedName, actualName);
                    return Ok(new { message = $"Filter {requestedName} not installed. Using {actualName}.", warning = true, filter = actualFilter, filterName = actualName });
                }

                var filterName = FtdxTenRoofingFilterNames.GetValueOrDefault(actualFilter, actualFilter);
                _logger.LogInformation("Set roofing filter (FTdx10) to {Filter}", filterName);
                return Ok(new { message = $"Roofing filter {filterName} selected", filter = actualFilter, filterName });
            }

            if (_radioStateService.ActiveVfo == 1) _radioStateService.RoofingFilterB = request.Filter;
            else                                   _radioStateService.RoofingFilterA = request.Filter;
            var fallbackName = FtdxTenRoofingFilterNames.GetValueOrDefault(request.Filter, request.Filter);
            return Ok(new { message = $"Roofing filter {fallbackName} selected", filter = request.Filter, filterName = fallbackName });
        }

        /// <summary>
        /// FTDX3000 single-receiver roofing filter: RF0 P2 set / RF0 P3 read.
        /// The read-back code (P3) uses a different value space than the set code
        /// (P2) — 600 Hz reads back as 7, 300 Hz as 8, and AUTO reports the
        /// filter in circuit (4/5/6/9/A) — so the read code is normalised back
        /// to the dropdown's set-code space. Per-VFO state is tracked in the
        /// active VFO slot. See <see cref="Ftdx3000Roofing"/>.
        /// </summary>
        private async Task<IActionResult> SetFtdx3000RoofingFilterAsync(RoofingFilterRequest request)
        {
            // P1 is always 0 (single receiver); the set code is the filter number directly.
            await _catClient.SendCommandAsync($"RF0{request.Filter};", "WebUI", CancellationToken.None);
            await Task.Delay(100);
            var readback = await _catClient.SendCommandAsync("RF0;", "WebUI", CancellationToken.None);

            var readCode = readback?.Length >= 4 ? readback[3].ToString() : request.Filter;
            var stateCode = Ftdx3000Roofing.NormalizeReadCode(readCode);
            var displayName = Ftdx3000Roofing.ReadCodeNames.GetValueOrDefault(readCode,
                              Ftdx3000Roofing.SetCodeNames.GetValueOrDefault(stateCode, stateCode));

            if (_radioStateService.ActiveVfo == 1) _radioStateService.RoofingFilterB = stateCode;
            else                                   _radioStateService.RoofingFilterA = stateCode;
            _logger.LogInformation("Set roofing filter (FTDX3000) to {Filter} (read code {ReadCode})", displayName, readCode);
            return Ok(new { message = $"Roofing filter set to {displayName}", filter = stateCode, filterName = displayName });
        }

        [HttpPost("mode/{receiver}")]
        public async Task<IActionResult> SetMode(string receiver, [FromBody] ModeRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();
                string displayMode = CatCodeToMode.TryGetValue(request.Mode, out var modeName) ? modeName : request.Mode;

                var recv = receiver.ToUpperInvariant();
                if (recv != "A" && recv != "B")
                    return BadRequest(new { error = "Invalid receiver specified" });

                await _catClient.SendCommandAsync($"MD{VfoP1Outgoing(recv)}{request.Mode};", "User");
                if (VfoIsB(recv)) _radioStateService.ModeB = displayMode;
                else               _radioStateService.ModeA = displayMode;

                // Re-apply Contour and APF state — mode changes on the FTdx101 cause the
                // radio to restore its per-mode Contour/APF settings, overriding what we have set.
                var modeSettings = await _settingsService.GetSettingsAsync();
                bool isFtdx3000 = modeSettings.RadioModel == "FTDX3000";
                bool targetB = VfoIsB(recv);
                // P1=0 on FTDX3000 (special CO format) and on every single-receiver
                // model (P1 Fixed=0). Dual-receiver -> P1 by VFO.
                string p1 = isFtdx3000 ? "0" : VfoP1Outgoing(recv);

                if (isFtdx3000)
                {
                    bool cOn = _radioStateService.ContourOnA;
                    bool aOn = _radioStateService.ApfOnA;
                    if (cOn)
                    {
                        await _catClient.SendCommandAsync("CO0001;", "User");
                        int vv = Math.Max(1, Math.Min(40, _radioStateService.ContourFreqA / 100));
                        await _catClient.SendCommandAsync($"CO01{vv:D2};", "User");
                    }
                    else if (aOn)
                    {
                        await _catClient.SendCommandAsync("CO0002;", "User");
                        int vv = Math.Max(0, Math.Min(20, (_radioStateService.ApfFreqA / 25) + 10));
                        await _catClient.SendCommandAsync($"CO02{vv:D2};", "User");
                    }
                    else
                    {
                        await _catClient.SendCommandAsync("CO0000;", "User");
                    }
                }
                else
                {
                    bool contourOn  = targetB ? _radioStateService.ContourOnB  : _radioStateService.ContourOnA;
                    int  contourHz  = targetB ? _radioStateService.ContourFreqB : _radioStateService.ContourFreqA;
                    bool apfOn      = targetB ? _radioStateService.ApfOnB       : _radioStateService.ApfOnA;
                    int  apfHz      = targetB ? _radioStateService.ApfFreqB     : _radioStateService.ApfFreqA;

                    int  cFreq = Math.Max(100, Math.Min(3200, contourHz));
                    await _catClient.SendCommandAsync($"CO{p1}0000{(contourOn ? 1 : 0)};", "User");
                    await _catClient.SendCommandAsync($"CO{p1}1{cFreq:D4};", "User");

                    int  aVvvv = Math.Max(0, Math.Min(50, (apfHz / 10) + 25));
                    await _catClient.SendCommandAsync($"CO{p1}2000{(apfOn ? 1 : 0)};", "User");
                    await _catClient.SendCommandAsync($"CO{p1}3{aVvvv:D4};", "User");
                }

                _logger.LogInformation("Sending CAT command: MD{Vfo}{Mode}; for Receiver {Receiver}", VfoP1Outgoing(recv), request.Mode, recv);
                return Ok(new { message = $"Mode {displayMode} selected for Receiver {receiver}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Receiver {Receiver} mode", receiver);
                return StatusCode(500, new { error = "Failed to set mode" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        [HttpPost("power/{receiver}")]
        public async Task<IActionResult> SetPower(string receiver, [FromBody] PowerRequest request)
        {
            _logger.LogInformation("[Slider][CAT] SetPower endpoint called: receiver={Receiver}, watts={Watts}", receiver, request.Watts);
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();

                var settings = await _settingsService.GetSettingsAsync();
                int maxPower = settings.RadioModel == "FTdx101MP" ? 200 : 100;

                _logger.LogInformation("[API] Received SetPower request: receiver={Receiver}, Watts={Watts}, Model={Model}", receiver, request.Watts, settings.RadioModel);
                _logger.LogInformation("[API] DEBUG: Received slider value = {Watts}", request.Watts);

                if (request.Watts < 5 || request.Watts > maxPower)
                    return BadRequest(new { error = $"Power out of range (5-{maxPower}W for {settings.RadioModel})" });

                var command = $"PC{request.Watts:D3};";
                _logger.LogInformation("[API] Sending CAT command: {Command}", command);
                await _catClient.SendCommandAsync(command, "WebUI", CancellationToken.None);
                // Immediately send PC; to read back power
                var readResponse = await _catClient.SendCommandAsync("PC;", "WebUI", CancellationToken.None);
                int actualPower = ParsePower(readResponse ?? "");
                _logger.LogInformation("[Slider][CAT] Sent PC command: watts={Watts}, readback={Readback}, actualPower={ActualPower}", request.Watts, readResponse, actualPower);

                _logger.LogInformation("[API] Setting Power to {ActualPower}", actualPower);
                _radioStateService.Power = actualPower;

                _logger.LogInformation("[API] Power set to {Power}W on {RadioModel}", actualPower, settings.RadioModel);
                return Ok(new { message = $"Power set to {actualPower}W", maxPower = maxPower });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting power");
                return StatusCode(500, new { error = "Failed to set power" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        // Add this helper method (put it near other helper methods like ParseSMeter)
        private int ParsePower(string response)
        {
            // Response format: PC123; (3 digits for watts)
            if (response.Length >= 5 && response.StartsWith("PC"))
            {
                if (int.TryParse(response.Substring(2, 3), out int watts))
                {
                    return watts;
                }
            }
            return 100; // Default to 100W if can't parse
        }

        [HttpPost("afgain")]
        public async Task<IActionResult> SetAfGain([FromBody] AfGainRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();
                if (request == null || (request.Band != "0" && request.Band != "1"))
                    return BadRequest(new { error = "Invalid band (must be '0' or '1')" });
                if (!int.TryParse(request.Value, out int val) || val < 0 || val > 255)
                    return BadRequest(new { error = "AF Gain value out of range (0-255)" });

                string command = $"AG{request.Band}{val:D3};";
                await _catClient.SendCommandAsync(command, "WebUI", CancellationToken.None);

                // Read back the actual AF Gain value from the radio
                string readCmd = request.Band == "0" ? "AG0;" : "AG1;";
                var response = await _catClient.SendCommandAsync(readCmd, "WebUI", CancellationToken.None);
                int actualValue = val;
                if (!string.IsNullOrEmpty(response) && response.Length >= 6)
                {
                    // Response format: AG0nnn; or AG1nnn;
                    var valueStr = response.Substring(3, 3);
                    if (int.TryParse(valueStr, out int parsed))
                        actualValue = parsed;
                }

                // Persist the actual value
                if (request.Band == "0")
                    _radioStateService.AfGainA = actualValue;
                else if (request.Band == "1")
                    _radioStateService.AfGainB = actualValue;
                _logger.LogInformation("Set AF Gain band {Band} to {Requested} (actual: {Actual})", request.Band, val, actualValue);
                if (actualValue != val)
                    _logger.LogWarning("AF Gain mismatch: requested {Requested}, radio returned {Actual}", val, actualValue);
                return Ok(new { message = $"AF Gain set to {actualValue} for band {request.Band}", actual = actualValue });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting AF Gain");
                return StatusCode(500, new { error = "Failed to set AF Gain" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        public class BandRequest { public string Band { get; set; } = string.Empty; }
        public class AntennaRequest { public string Antenna { get; set; } = string.Empty; }
        public class ModeRequest { public string Mode { get; set; } = string.Empty; }
        public class FrequencyRequest { public long FrequencyHz { get; set; } }
        public class PowerRequest
        {
            public int Watts { get; set; }
        }

        public class MicGainRequest
        {
            public int Value { get; set; }
        }

        public class ProcRequest
        {
            public bool Enabled { get; set; }
        }

        public class ProcLevelRequest
        {
            public int Value { get; set; }
        }

        public class AfGainRequest
        {
            public string Band { get; set; } = string.Empty;
            public string Value { get; set; } = string.Empty;
        }

        public class RadioPowerRequest
        {
            public bool PowerOn { get; set; }
        }

        public class TxRequest
        {
            public bool Transmit { get; set; }
        }

        public class RoofingFilterRequest
        {
            public string Filter { get; set; } = string.Empty;
        }

        public class AgcRequest        { public string Code { get; set; } = string.Empty; }
        public class IpoRequest         { public string Code { get; set; } = string.Empty; }
        public class AutoNotchRequest   { public string Code { get; set; } = string.Empty; }
        public class NrRequest          { public string Code { get; set; } = string.Empty; }
        public class AttenuatorRequest  { public string Code { get; set; } = string.Empty; }
        public class ManualNotchRequest    { public string Enabled { get; set; } = "0"; }
        public class NoiseBlankerRequest       { public string Enabled { get; set; } = "0"; }
        public class ManualNotchFreqRequest    { public int FrequencyHz { get; set; } = 1000; }
        public class IfWidthRequest            { public string Code { get; set; } = "8"; }
        public class IfShiftRequest            { public int ShiftHz { get; set; } = 0; }

        [HttpPost("agc/{receiver}")]
        public async Task<IActionResult> SetAgc(string receiver, [FromBody] AgcRequest request)
        {
            var validCodes = new[] { "0", "1", "2", "3", "4" };
            if (!validCodes.Contains(request.Code))
                return BadRequest(new { error = $"Invalid AGC code: {request.Code}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"GT{VfoP1Outgoing(receiver)}{request.Code};", "WebUI", CancellationToken.None);

                if (VfoIsB(receiver)) _radioStateService.AgcB = request.Code;
                else                  _radioStateService.AgcA = request.Code;

                return Ok(new { message = $"AGC {receiver} set to {request.Code}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting AGC");
                return StatusCode(500, new { error = "Failed to set AGC" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        [HttpPost("ipo/{receiver}")]
        public async Task<IActionResult> SetIpo(string receiver, [FromBody] IpoRequest request)
        {
            var validCodes = new[] { "0", "1", "2" };
            if (!validCodes.Contains(request.Code))
                return BadRequest(new { error = $"Invalid IPO/AMP code: {request.Code}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"PA{VfoP1Outgoing(receiver)}{request.Code};", "WebUI", CancellationToken.None);

                if (VfoIsB(receiver)) _radioStateService.IpoB = request.Code;
                else                  _radioStateService.IpoA = request.Code;

                return Ok(new { message = $"IPO/AMP {receiver} set to {request.Code}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting IPO/AMP");
                return StatusCode(500, new { error = "Failed to set IPO/AMP" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("autonotch/{receiver}")]
        public async Task<IActionResult> SetAutoNotch(string receiver, [FromBody] AutoNotchRequest request)
        {
            var validCodes = new[] { "0", "1" };
            if (!validCodes.Contains(request.Code))
                return BadRequest(new { error = $"Invalid Auto Notch code: {request.Code}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"BC{VfoP1Outgoing(receiver)}{request.Code};", "WebUI", CancellationToken.None);

                if (VfoIsB(receiver)) _radioStateService.AutoNotchB = request.Code;
                else                  _radioStateService.AutoNotchA = request.Code;

                return Ok(new { message = $"Auto Notch {receiver} set to {request.Code}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Auto Notch");
                return StatusCode(500, new { error = "Failed to set Auto Notch" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("nr/{receiver}")]
        public async Task<IActionResult> SetNr(string receiver, [FromBody] NrRequest request)
        {
            var validCodes = new[] { "0", "1", "2" };
            if (!validCodes.Contains(request.Code))
                return BadRequest(new { error = $"Invalid NR code: {request.Code}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"NR{VfoP1Outgoing(receiver)}{request.Code};", "WebUI", CancellationToken.None);

                if (VfoIsB(receiver)) _radioStateService.NrB = request.Code;
                else                  _radioStateService.NrA = request.Code;

                return Ok(new { message = $"NR {receiver} set to {request.Code}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Noise Reduction");
                return StatusCode(500, new { error = "Failed to set Noise Reduction" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("attenuator/{receiver}")]
        public async Task<IActionResult> SetAttenuator(string receiver, [FromBody] AttenuatorRequest request)
        {
            var validCodes = new[] { "00", "06", "12", "18" };
            if (!validCodes.Contains(request.Code))
                return BadRequest(new { error = $"Invalid attenuator code: {request.Code}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();
                var catCode = request.Code switch { "00" => "0", "06" => "1", "12" => "2", "18" => "3", _ => "0" };
                await _catClient.SendCommandAsync($"RA{VfoP1Outgoing(receiver)}{catCode};", "WebUI", CancellationToken.None);

                if (VfoIsB(receiver)) _radioStateService.AttB = request.Code;
                else                  _radioStateService.AttA = request.Code;

                return Ok(new { message = $"Attenuator {receiver} set to {request.Code}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Attenuator");
                return StatusCode(500, new { error = "Failed to set Attenuator" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("manualnotch/{receiver}")]
        public async Task<IActionResult> SetManualNotch(string receiver, [FromBody] ManualNotchRequest request)
        {
            var validValues = new[] { "0", "1" };
            if (!validValues.Contains(request.Enabled))
                return BadRequest(new { error = $"Invalid Manual Notch value: {request.Enabled}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();
                var val = request.Enabled == "1" ? "001" : "000";
                await _catClient.SendCommandAsync($"BP{VfoP1Outgoing(receiver)}0{val};", "WebUI", CancellationToken.None);

                if (VfoIsB(receiver)) _radioStateService.ManualNotchB = request.Enabled;
                else                  _radioStateService.ManualNotchA = request.Enabled;

                return Ok(new { message = $"Manual Notch {receiver} set to {request.Enabled}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Manual Notch");
                return StatusCode(500, new { error = "Failed to set Manual Notch" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("manualnotchfreq/{receiver}")]
        public async Task<IActionResult> SetManualNotchFreq(string receiver, [FromBody] ManualNotchFreqRequest request)
        {
            if (request.FrequencyHz < 10 || request.FrequencyHz > 3200)
                return BadRequest(new { error = $"Notch frequency must be 10–3200 Hz" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();
                var catValue = request.FrequencyHz / 10;
                await _catClient.SendCommandAsync($"BP{VfoP1Outgoing(receiver)}1{catValue:D3};", "WebUI", CancellationToken.None);

                if (VfoIsB(receiver)) _radioStateService.ManualNotchFreqB = request.FrequencyHz;
                else                  _radioStateService.ManualNotchFreqA = request.FrequencyHz;

                return Ok(new { message = $"Manual Notch freq {receiver} set to {request.FrequencyHz} Hz" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Manual Notch frequency");
                return StatusCode(500, new { error = "Failed to set Manual Notch frequency" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("noiseblanker/{receiver}")]
        public async Task<IActionResult> SetNoiseBlanker(string receiver, [FromBody] NoiseBlankerRequest request)
        {
            if (request.Enabled != "0" && request.Enabled != "1")
                return BadRequest(new { error = $"Invalid Noise Blanker value: {request.Enabled}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"NB{VfoP1Outgoing(receiver)}{request.Enabled};", "WebUI", CancellationToken.None);

                if (VfoIsB(receiver)) _radioStateService.NbB = request.Enabled;
                else                  _radioStateService.NbA = request.Enabled;

                return Ok(new { message = $"Noise Blanker {receiver} set to {request.Enabled}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Noise Blanker");
                return StatusCode(500, new { error = "Failed to set Noise Blanker" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // GET — query the radio for its current IF Width code and refresh
        // RadioStateService. Used for live calibration discovery: the user
        // changes WIDTH on the radio's front panel, then hits this URL to
        // see what SH code came back. Returns 99 max to allow probing codes
        // beyond the official documented range (post-firmware extensions).
        [HttpGet("ifwidth/{receiver}")]
        public async Task<IActionResult> QueryIfWidth(string receiver)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                var p1 = VfoP1Outgoing(receiver);
                var response = await _catClient.SendCommandAsync($"SH{p1};", "WebUI", CancellationToken.None);
                // The dispatcher will have updated RadioStateService.IfWidthA/B by now.
                var current = VfoIsB(receiver) ? _radioStateService.IfWidthB : _radioStateService.IfWidthA;
                return Ok(new { vfo = receiver.ToUpper(), code = current, rawResponse = response });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error querying IF Width");
                return StatusCode(500, new { error = "Failed to query IF Width" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("ifwidth/{receiver}")]
        public async Task<IActionResult> SetIfWidth(string receiver, [FromBody] IfWidthRequest request)
        {
            // 0-99 allows probing post-firmware codes beyond the official 0-25 range.
            if (!int.TryParse(request.Code, out int codeNum) || codeNum < 0 || codeNum > 99)
                return BadRequest(new { error = $"Invalid IF Width code: {request.Code}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"SH{VfoP1Outgoing(receiver)}0{int.Parse(request.Code):D2};", "WebUI", CancellationToken.None);
                if (VfoIsB(receiver)) _radioStateService.IfWidthB = request.Code;
                else                  _radioStateService.IfWidthA = request.Code;
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting IF Width");
                return StatusCode(500, new { error = "Failed to set IF Width" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("ifshift/{receiver}")]
        public async Task<IActionResult> SetIfShift(string receiver, [FromBody] IfShiftRequest request)
        {
            if (request.ShiftHz < -1000 || request.ShiftHz > 1000)
                return BadRequest(new { error = "IF Shift must be -1000 to +1000 Hz" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                var sign = request.ShiftHz >= 0 ? '+' : '-';
                var abs = Math.Abs(request.ShiftHz);
                await _catClient.SendCommandAsync($"IS{VfoP1Outgoing(receiver)}0{sign}{abs:D4};", "WebUI", CancellationToken.None);
                if (VfoIsB(receiver)) _radioStateService.IfShiftB = request.ShiftHz;
                else                  _radioStateService.IfShiftA = request.ShiftHz;
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting IF Shift");
                return StatusCode(500, new { error = "Failed to set IF Shift" });
            }
            finally { _requestSemaphore.Release(); }
        }

        public class ContourRequest
        {
            public bool On { get; set; }
            public int FreqHz { get; set; } = 800;
        }

        public class ApfRequest
        {
            public bool On { get; set; }
            public int FreqHz { get; set; } = 0;
        }

        public class ClarifierRequest
        {
            public string Vfo { get; set; } = "A";
            public bool RxOn { get; set; }
            public bool TxOn { get; set; }
            public int OffsetHz { get; set; }
        }

        public class ClarifierNudgeRequest
        {
            public string Vfo { get; set; } = "A";
            public int DeltaHz { get; set; }
        }

        [HttpPost("contour/{receiver}")]
        public async Task<IActionResult> SetContour(string receiver, [FromBody] ContourRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                var settings = await _settingsService.GetSettingsAsync();
                bool isFtdx3000 = settings.RadioModel == "FTDX3000";
                // VfoP1Outgoing forces "0" on every single-receiver model
                // (including FTDX3000 which is also single-receiver per
                // RadioCapabilities), so the FTDX3000 special-case can use
                // it too -- the original code's special-case existed because
                // the CO command itself has a different shape on FTDX3000,
                // not because of P1 routing differences.
                string p1 = isFtdx3000 ? "0" : VfoP1Outgoing(receiver);

                if (isFtdx3000)
                {
                    // Mode: 00=off, 01=contour on, 02=APF on
                    string mode = request.On ? "01" : "00";
                    await _catClient.SendCommandAsync($"CO00{mode};", "WebUI", CancellationToken.None);
                    int vv = Math.Max(1, Math.Min(40, request.FreqHz / 100));
                    await _catClient.SendCommandAsync($"CO01{vv:D2};", "WebUI", CancellationToken.None);
                }
                else
                {
                    int freq = Math.Max(100, Math.Min(3200, request.FreqHz));
                    await _catClient.SendCommandAsync($"CO{p1}0000{(request.On ? 1 : 0)};", "WebUI", CancellationToken.None);
                    await _catClient.SendCommandAsync($"CO{p1}1{freq:D4};", "WebUI", CancellationToken.None);
                }

                if (VfoIsB(receiver)) { _radioStateService.ContourOnB = request.On; _radioStateService.ContourFreqB = request.FreqHz; }
                else                  { _radioStateService.ContourOnA = request.On; _radioStateService.ContourFreqA = request.FreqHz; }

                if (isFtdx3000 && request.On)
                {
                    _radioStateService.ApfOnA = false;
                    _radioStateService.ApfOnB = false;
                }

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Contour");
                return StatusCode(500, new { error = "Failed to set Contour" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("apf/{receiver}")]
        public async Task<IActionResult> SetApf(string receiver, [FromBody] ApfRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                var settings = await _settingsService.GetSettingsAsync();
                bool isFtdx3000 = settings.RadioModel == "FTDX3000";
                string p1 = isFtdx3000 ? "0" : VfoP1Outgoing(receiver);

                if (isFtdx3000)
                {
                    string mode = request.On ? "02" : "00";
                    await _catClient.SendCommandAsync($"CO00{mode};", "WebUI", CancellationToken.None);
                    int vv = Math.Max(0, Math.Min(20, (request.FreqHz / 25) + 10));
                    await _catClient.SendCommandAsync($"CO02{vv:D2};", "WebUI", CancellationToken.None);
                }
                else
                {
                    int vvvv = Math.Max(0, Math.Min(50, (request.FreqHz / 10) + 25));
                    await _catClient.SendCommandAsync($"CO{p1}2000{(request.On ? 1 : 0)};", "WebUI", CancellationToken.None);
                    await _catClient.SendCommandAsync($"CO{p1}3{vvvv:D4};", "WebUI", CancellationToken.None);
                }

                if (VfoIsB(receiver)) { _radioStateService.ApfOnB = request.On; _radioStateService.ApfFreqB = request.FreqHz; }
                else                  { _radioStateService.ApfOnA = request.On; _radioStateService.ApfFreqA = request.FreqHz; }

                if (isFtdx3000 && request.On)
                {
                    _radioStateService.ContourOnA = false;
                    _radioStateService.ContourOnB = false;
                }

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting APF");
                return StatusCode(500, new { error = "Failed to set APF" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("clarifier")]
        public async Task<IActionResult> SetClarifier([FromBody] ClarifierRequest request)
        {
            if (request.OffsetHz < -9990 || request.OffsetHz > 9990)
                return BadRequest(new { error = "Clarifier offset must be -9990 to +9990 Hz" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                var settings = await _settingsService.GetSettingsAsync();
                bool useCf = settings.RadioModel is "FTdx10" or "FT-710";
                string p1 = request.Vfo == "B" ? "1" : "0";

                if (useCf)
                {
                    int rxBit = request.RxOn ? 1 : 0;
                    int txBit = request.TxOn ? 1 : 0;
                    await _catClient.SendCommandAsync($"CF{p1}00{rxBit}{txBit}000;", "WebUI", CancellationToken.None);
                    string sign = request.OffsetHz >= 0 ? "+" : "-";
                    await _catClient.SendCommandAsync($"CF{p1}01{sign}{Math.Abs(request.OffsetHz):D4};", "WebUI", CancellationToken.None);
                }
                else
                {
                    await _catClient.SendCommandAsync($"RT{(request.RxOn ? 1 : 0)};", "WebUI", CancellationToken.None);
                    await _catClient.SendCommandAsync($"XT{(request.TxOn ? 1 : 0)};", "WebUI", CancellationToken.None);
                    await _catClient.SendCommandAsync("RC;", "WebUI", CancellationToken.None);
                    if (request.OffsetHz > 0)
                        await _catClient.SendCommandAsync($"RU{request.OffsetHz:D4};", "WebUI", CancellationToken.None);
                    else if (request.OffsetHz < 0)
                        await _catClient.SendCommandAsync($"RD{Math.Abs(request.OffsetHz):D4};", "WebUI", CancellationToken.None);
                }

                if (request.Vfo == "B") _radioStateService.ClarifierOffsetB = request.OffsetHz;
                else                     _radioStateService.ClarifierOffsetA = request.OffsetHz;
                _radioStateService.RxClarOn = request.RxOn;
                _radioStateService.TxClarOn = request.TxOn;
                return Ok(new { message = "Clarifier updated" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting clarifier");
                return StatusCode(500, new { error = "Failed to set clarifier" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("clarifier/nudge")]
        public async Task<IActionResult> NudgeClarifier([FromBody] ClarifierNudgeRequest request)
        {
            int absHz = Math.Abs(request.DeltaHz);
            if (absHz == 0 || absHz > 9990)
                return BadRequest(new { error = "DeltaHz must be 1–9990 Hz" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                var settings = await _settingsService.GetSettingsAsync();
                bool useCf = settings.RadioModel is "FTdx10" or "FT-710";
                string p1 = request.Vfo == "B" ? "1" : "0";

                int currentOffset = request.Vfo == "B" ? _radioStateService.ClarifierOffsetB : _radioStateService.ClarifierOffsetA;
                int newOffset = Math.Max(-9990, Math.Min(9990, currentOffset + request.DeltaHz));

                if (useCf)
                {
                    string sign = newOffset >= 0 ? "+" : "-";
                    await _catClient.SendCommandAsync($"CF{p1}01{sign}{Math.Abs(newOffset):D4};", "WebUI", CancellationToken.None);
                }
                else
                {
                    // RU/RD are incremental — send only the delta, no RC clear
                    if (request.DeltaHz > 0)
                        await _catClient.SendCommandAsync($"RU{absHz:D4};", "WebUI", CancellationToken.None);
                    else
                        await _catClient.SendCommandAsync($"RD{absHz:D4};", "WebUI", CancellationToken.None);
                }

                if (request.Vfo == "B") _radioStateService.ClarifierOffsetB = newOffset;
                else                     _radioStateService.ClarifierOffsetA = newOffset;
                return Ok(new { offsetHz = newOffset });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error nudging clarifier");
                return StatusCode(500, new { error = "Failed to nudge clarifier" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("clarifier/reset")]
        public async Task<IActionResult> ResetClarifier([FromBody] ClarifierRequest request)
        {
            request.RxOn = true;
            request.TxOn = false;
            request.OffsetHz = 0;
            return await SetClarifier(request);
        }

        [HttpPost("split/{mode}")]
        public async Task<IActionResult> SetSplit(int mode)
        {
            if (mode < 0 || mode > 2)
                return BadRequest(new { error = "Split mode must be 0 (off), 1 (on), or 2 (quick split +5 kHz)" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();

                // FTDX3000 has no ST command (confirmed no-op by iu1teu/Giovanni on
                // #78) — split is driven by FT instead: FT2; = TX on VFO A (no split,
                // TX=RX), FT3; = TX on VFO B (split). Read-back FT; answers FT0 (TX=A)
                // / FT1 (TX=B). The other supported models (FTdx101/FTdx10/FT-710) all
                // have ST, so they fall through to the ST path below.
                var splitSettings = await _settingsService.GetSettingsAsync();
                if (splitSettings.RadioModel == "FTDX3000")
                {
                    if (mode == 2)
                    {
                        // Quick split: VFO B = VFO A + 5 kHz, then transmit on B.
                        var faQs = await _catClient.SendCommandAsync("FA;", "WebUI", CancellationToken.None);
                        if (!string.IsNullOrWhiteSpace(faQs) && faQs.StartsWith("FA") &&
                            long.TryParse(faQs.Substring(2).TrimEnd(';'), out long freqAqs))
                        {
                            long freqBqs = Math.Min(freqAqs + 5000, 75_000_000);
                            await _catClient.SendCommandAsync($"FB{freqBqs:D9};", "WebUI", CancellationToken.None);
                            _radioStateService.FrequencyB = freqBqs;
                        }
                    }

                    bool ftSplitOn = mode != 0;               // mode 1 or 2 → split on
                    await _catClient.SendCommandAsync(ftSplitOn ? "FT3;" : "FT2;", "WebUI", CancellationToken.None);

                    var ftResp = await _catClient.SendCommandAsync("FT;", "WebUI", CancellationToken.None);
                    int ftTxVfo = ftSplitOn ? 1 : 0;
                    if (!string.IsNullOrWhiteSpace(ftResp) && ftResp.StartsWith("FT") && ftResp.Length >= 3)
                        ftTxVfo = ftResp[2] == '1' ? 1 : 0;
                    _radioStateService.TxVfo = ftTxVfo;
                    int ftSplitMode = ftTxVfo == 1 ? 1 : 0;
                    _radioStateService.SplitMode = ftSplitMode;
                    _logger.LogInformation("Split (FTDX3000) via FT: TX VFO = {TxVfo}, splitMode = {Mode}",
                        ftTxVfo == 1 ? "B" : "A", ftSplitMode);
                    return Ok(new { splitMode = ftSplitMode });
                }

                if (mode == 2)
                {
                    // Quick Split: implement explicitly so it works whether split is already on or off.
                    // ST2; on the FTdx101 only executes the +5 kHz part when transitioning from off→on;
                    // if split is already active the radio ignores the frequency offset. So we read
                    // VFO A, compute +5 kHz, set VFO B directly, then enable split.
                    var faResponse = await _catClient.SendCommandAsync("FA;", "WebUI", CancellationToken.None);
                    if (!string.IsNullOrWhiteSpace(faResponse) && faResponse.StartsWith("FA") &&
                        long.TryParse(faResponse.Substring(2).TrimEnd(';'), out long freqA))
                    {
                        long freqB = Math.Min(freqA + 5000, 75_000_000);
                        await _catClient.SendCommandAsync($"FB{freqB:D9};", "WebUI", CancellationToken.None);
                        _radioStateService.FrequencyB = freqB;
                    }
                    await _catClient.SendCommandAsync("ST1;", "WebUI", CancellationToken.None);
                    _radioStateService.SplitMode = 1;
                    _logger.LogInformation("Quick Split: VFO B set to VFO A + 5 kHz, split ON");
                    return Ok(new { splitMode = 1 });
                }

                await _catClient.SendCommandAsync($"ST{mode};", "WebUI", CancellationToken.None);

                // Read back actual state
                var stResponse = await _catClient.SendCommandAsync("ST;", "WebUI", CancellationToken.None);
                int actualMode = mode;
                if (!string.IsNullOrWhiteSpace(stResponse) && stResponse.StartsWith("ST"))
                    int.TryParse(stResponse.Substring(2, 1), out actualMode);
                _radioStateService.SplitMode = actualMode;

                _logger.LogInformation("Split mode set to {Mode}", actualMode);
                return Ok(new { splitMode = actualMode });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting split mode");
                return StatusCode(500, new { error = "Failed to set split mode" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // Set the active/operating band on dual-receiver radios (FTdx101).
        // VS selects which band the main tuning knob controls: 0 = MAIN (VFO A),
        // 1 = SUB (VFO B). The radio auto-broadcasts VS, so the UI highlight
        // follows automatically; we also set ActiveVfo here to avoid UI flicker.
        [HttpPost("active-vfo/{vfo}")]
        public async Task<IActionResult> SetActiveVfo(string vfo)
        {
            var v = vfo.ToUpperInvariant();
            if (v != "A" && v != "B")
                return BadRequest(new { error = "VFO must be A or B" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                var vs = v == "B" ? 1 : 0;
                await _catClient.SendCommandAsync($"VS{vs};", "WebUI", CancellationToken.None);
                _radioStateService.ActiveVfo = vs;
                _logger.LogInformation("Active VFO set to {Vfo} (VS{Vs})", v, vs);
                return Ok(new { activeVfo = vs });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting active VFO");
                return StatusCode(500, new { error = "Failed to set active VFO" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("swap-vfo")]
        public async Task<IActionResult> SwapVfo()
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync("SV;", "WebUI", CancellationToken.None);

                // Read back both frequencies immediately — auto-info will also arrive but this avoids UI flicker
                var faResponse = await _catClient.SendCommandAsync("FA;", "WebUI", CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(faResponse) && faResponse.StartsWith("FA") &&
                    long.TryParse(faResponse.Substring(2).TrimEnd(';'), out long freqA))
                {
                    _radioStateService.FrequencyA = freqA;
                }

                var fbResponse = await _catClient.SendCommandAsync("FB;", "WebUI", CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(fbResponse) && fbResponse.StartsWith("FB") &&
                    long.TryParse(fbResponse.Substring(2).TrimEnd(';'), out long freqB))
                {
                    _radioStateService.FrequencyB = freqB;
                }

                // Re-query ATU state. On single-receiver radios like the
                // FTdx10 the AC command is global at the CAT layer, but the
                // radio firmware stores ATU on/off per-VFO internally and
                // re-applies the now-active VFO's value after SV;. Reading
                // it back here keeps YWC's display in sync. See issue #34
                // discussion with Jacek SP3L for the empirical evidence.
                //
                // We parse the reply directly because the multiplexer
                // consumes direct command replies into _pendingResponses
                // before they reach CatMessageDispatcher.
                try
                {
                    var acResponse = await _catClient.SendCommandAsync("AC;", "WebUI", CancellationToken.None);
                    if (!string.IsNullOrEmpty(acResponse) && acResponse.StartsWith("AC") && acResponse.Length >= 5)
                    {
                        _radioStateService.AtuEnabled = acResponse[4] == '1';
                    }
                    _logger.LogDebug("Post-swap AC; reply: {Reply}", acResponse ?? "(null)");
                }
                catch (Exception acEx)
                {
                    _logger.LogDebug(acEx, "Post-swap AC; query failed (non-fatal)");
                }

                _logger.LogInformation("VFO A and VFO B swapped");
                return Ok(new { message = "VFO swapped" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error swapping VFO");
                return StatusCode(500, new { error = "Failed to swap VFO" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // POST /api/cat/copy-vfo/{direction}
        //   direction = "ba" → copy VFO B to VFO A (Yaesu BA; CAT command)
        //   direction = "ab" → copy VFO A to VFO B (Yaesu AB; CAT command)
        //
        // Differs from swap (SV) in that it does NOT exchange — the source
        // VFO keeps its value. Useful for transmitting on VFO B's frequency
        // without enabling split: copy B→A and the radio's normal TX (which
        // uses VFO A) is now on the desired frequency.
        [HttpPost("copy-vfo/{direction}")]
        public async Task<IActionResult> CopyVfo(string direction)
        {
            var dir = (direction ?? "").ToLowerInvariant();
            if (dir != "ba" && dir != "ab")
                return BadRequest(new { error = "direction must be 'ba' or 'ab'" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                var cmd = dir == "ba" ? "BA;" : "AB;";
                await _catClient.SendCommandAsync(cmd, "WebUI", CancellationToken.None);

                // Read back both frequencies so the UI reflects the new state immediately.
                var faResponse = await _catClient.SendCommandAsync("FA;", "WebUI", CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(faResponse) && faResponse.StartsWith("FA") &&
                    long.TryParse(faResponse.Substring(2).TrimEnd(';'), out long freqA))
                {
                    _radioStateService.FrequencyA = freqA;
                }
                var fbResponse = await _catClient.SendCommandAsync("FB;", "WebUI", CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(fbResponse) && fbResponse.StartsWith("FB") &&
                    long.TryParse(fbResponse.Substring(2).TrimEnd(';'), out long freqB))
                {
                    _radioStateService.FrequencyB = freqB;
                }

                _logger.LogInformation("VFO copy {Dir} completed", dir.ToUpperInvariant());
                return Ok(new { message = $"VFO {dir.ToUpperInvariant()} copy completed" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error copying VFO ({Dir})", dir);
                return StatusCode(500, new { error = "Failed to copy VFO" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("reinitialize")]
        public async Task<IActionResult> Reinitialize()
        {
            // Test Connection ("Reinitialize") used to call the full
            // RadioInitializationService.InitializeRadioAsync() — the same
            // heavyweight startup sequence the app runs at launch (multiplexer
            // connect + ~30 read queries + state restoration, takes 5+ seconds).
            // That worked the first time the user clicked it (cold install) but
            // CRASHED YWC entirely when clicked while everything was running:
            // the deep init races with MeterPollingService at 10 Hz, the SDR
            // workers, in-flight WebUI commands, etc. Reported by Colin on
            // v2.3.3 — first click reported a false "radio not responding"
            // (from the race), second click hard-crashed the process so the
            // browser saw "Failed to fetch".
            //
            // Replacement logic: if the multiplexer is already connected (the
            // overwhelmingly common case — Test Connection is normally pressed
            // to verify a working setup), just send the ID; probe through the
            // existing CAT client. The multiplexer handles command queuing
            // correctly, so the probe coexists peacefully with meter polling.
            //
            // Only run the heavyweight init if the multiplexer is genuinely
            // disconnected (user changed Settings, plugged in the radio, and
            // wants Test Connection to attempt a fresh connection) — that's
            // the original recover-from-broken-state use case.
            try
            {
                _logger.LogInformation("Test Connection requested from Settings page (IsConnected={IsConnected})", _catClient.IsConnected);

                if (!_catClient.IsConnected)
                {
                    _logger.LogInformation("Test Connection: not currently connected — running full radio initialization");
                    await _radioInitService.InitializeRadioAsync();
                }

                // Verify the radio is actually responding. Standard Yaesu
                // identification probe ID; — require a parseable reply that
                // starts with 'ID' and includes a semicolon (e.g. 'ID0570;'
                // from an FTdx101MP). 2s timeout gives the multiplexer's
                // command queue plenty of room behind a busy meter poll.
                //
                // Without this check the button reported success whenever
                // SerialPort.Open() succeeded, even when the radio was not
                // actually responding to CAT (Juergen WB4EM, Disc #14: a
                // virtual-port-sharer in the chain swallowed the chatter but
                // Open() still succeeded, so the user was falsely reassured).
                string? probe = null;
                try
                {
                    probe = await _catClient.SendCommandAsync("ID;", "TestConnection", CancellationToken.None, timeoutMs: 2000);
                }
                catch (Exception probeEx)
                {
                    _logger.LogWarning(probeEx, "Test Connection: ID; probe threw");
                }

                // Probe must start with 'ID' and include at least the 4-character
                // radio-identifier code (e.g. 'ID0682' for FTdx101MP, 'ID0570'
                // for FTdx101D, etc). We do NOT require a trailing semicolon —
                // the multiplexer strips the CAT terminator as part of response
                // parsing, so what we see here is e.g. 'ID0682' even though the
                // wire response was 'ID0682;'. v2.3.3/v2.3.4 had a Contains(';')
                // check that always failed against the multiplexer's parsed
                // output — producing the false-negative "Radio did not respond"
                // even when CAT was working perfectly. Reported by Colin via
                // the log at 18:16, after the v2.3.4 crash fix landed but the
                // probe still reported failure.
                bool probeOk = !string.IsNullOrEmpty(probe)
                    && probe.StartsWith("ID", StringComparison.Ordinal)
                    && probe.Length >= 6;
                if (!probeOk)
                {
                    _logger.LogWarning(
                        "Test Connection: ID; probe failed. Reply='{Probe}' (null/empty/garbled means CAT is not actually reaching the radio).",
                        probe ?? "(null)");
                    return Ok(new
                    {
                        success = false,
                        message = "Radio did not respond to a CAT probe. " +
                                  "Check the radio is powered on, CAT is enabled in the radio's menu, " +
                                  "and the COM port is connected directly to the radio (not via a " +
                                  "virtual-port sharer like VSPE, OmniRig or com0com).",
                    });
                }

                var idCode = probe!.StartsWith("ID", StringComparison.Ordinal) ? probe.Substring(2).TrimEnd(';') : probe;
                _logger.LogInformation("Test Connection: probe OK — radio replied '{Probe}'", probe);
                return Ok(new { success = true, message = $"Connection succeeded — radio ID {idCode}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Test Connection failed");
                return Ok(new { success = false, message = ex.Message });
            }
        }

        // --- ATU ---
        public class AtuRequest { public bool Enabled { get; set; } }

        [HttpPost("atu")]
        public async Task<IActionResult> SetAtu([FromBody] AtuRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                // AC P1 P2 P3 ;  — P3=1 ATU ON, P3=0 ATU OFF (P1/P2 always 0)
                string cmd = request.Enabled ? "AC001;" : "AC000;";
                await _catClient.SendCommandAsync(cmd, "WebUI", CancellationToken.None);
                _radioStateService.AtuEnabled = request.Enabled;
                return Ok(new { atuEnabled = request.Enabled });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting ATU");
                return StatusCode(500, new { error = "Failed to set ATU" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // Auto-tune trigger. Sending AC002; toggles the radio's auto-tune
        // cycle — once to start, once to stop. The UI binds this to a
        // long-press of the ATU button.
        //
        // The Yaesu CAT manuals (FTdx10 and FTdx101MP both, page 6) say
        // P2 of AC is "Fixed at 0" — so the radio does NOT report tuning-
        // in-progress over CAT. The "Tuning…" UI state therefore has to be
        // managed client-side: the frontend assumes tuning is active for a
        // safe upper-bound window after pressing, then triggers a state
        // refresh via the GET endpoint below to capture the settled on/off.
        [HttpPost("atu/tune")]
        public async Task<IActionResult> StartAtuTune()
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync("AC002;", "WebUI", CancellationToken.None);
                _logger.LogInformation("ATU auto-tune toggled (AC002;)");
                return Ok(new { message = "ATU tune toggled" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error toggling ATU auto-tune");
                return StatusCode(500, new { error = "Failed to toggle ATU tune" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // Refresh the ATU on/off state by querying AC; from the radio.
        // Called by the frontend after a tune cycle in case the radio
        // didn't auto-report the settled state.
        //
        // Important: CatMultiplexerService.OnMessageReceived consumes
        // direct command replies into _pendingResponses BEFORE they reach
        // CatMessageDispatcher (only unsolicited auto-info messages flow
        // through the dispatcher). So we have to parse the AC reply
        // ourselves and update RadioStateService directly here.
        //
        // Reply format after the multiplexer strips the trailing ';':
        //   "AC" P1 P2 P3   — 5 characters, P3 at index 4.
        //   P3: 0=Tuner OFF, 1=Tuner ON, 2=Tuning Start/Stop
        [HttpGet("atu")]
        public async Task<IActionResult> RefreshAtuState()
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                var reply = await _catClient.SendCommandAsync("AC;", "WebUI", CancellationToken.None);
                if (!string.IsNullOrEmpty(reply) && reply.StartsWith("AC") && reply.Length >= 5)
                {
                    _radioStateService.AtuEnabled = reply[4] == '1';
                    _logger.LogDebug("ATU refresh: parsed AtuEnabled={State} from reply '{Reply}'",
                        _radioStateService.AtuEnabled, reply);
                }
                else
                {
                    _logger.LogWarning("ATU refresh: unexpected AC; reply '{Reply}' (not parsed)", reply ?? "(null)");
                }
                return Ok(new { atuEnabled = _radioStateService.AtuEnabled });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing ATU state");
                return StatusCode(500, new { error = "Failed to refresh ATU state" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // --- NB LEVEL ---
        public class NbLevelRequest { public int Level { get; set; } = 10; }

        [HttpPost("nblevel/{receiver}")]
        public async Task<IActionResult> SetNbLevel(string receiver, [FromBody] NbLevelRequest request)
        {
            if (request.Level < 1 || request.Level > 20)
                return BadRequest(new { error = "NB level must be 1–20" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"NL{VfoP1Outgoing(receiver)}{request.Level:D3};", "WebUI", CancellationToken.None);
                if (VfoIsB(receiver)) _radioStateService.NbLevelB = request.Level;
                else                  _radioStateService.NbLevelA = request.Level;
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting NB level");
                return StatusCode(500, new { error = "Failed to set NB level" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // --- NR LEVEL (DNR algorithm on FTdx10) ---
        public class NrLevelRequest { public int Level { get; set; } = 1; }

        [HttpPost("nrlevel/{receiver}")]
        public async Task<IActionResult> SetNrLevel(string receiver, [FromBody] NrLevelRequest request)
        {
            if (request.Level < 1 || request.Level > 15)
                return BadRequest(new { error = "NR level must be 1–15" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"RL{VfoP1Outgoing(receiver)}{request.Level:D2};", "WebUI", CancellationToken.None);
                if (VfoIsB(receiver)) _radioStateService.NrLevelB = request.Level;
                else                  _radioStateService.NrLevelA = request.Level;
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting NR level");
                return StatusCode(500, new { error = "Failed to set NR level" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // --- CW PITCH ---
        public class CwPitchRequest { public int Code { get; set; } = 30; }

        [HttpPost("cwpitch")]
        public async Task<IActionResult> SetCwPitch([FromBody] CwPitchRequest request)
        {
            if (request.Code < 0 || request.Code > 75)
                return BadRequest(new { error = "CW pitch code must be 0–75 (300–1050 Hz)" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"KP{request.Code:D2};", "WebUI", CancellationToken.None);
                _radioStateService.CwPitch = request.Code;
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting CW pitch");
                return StatusCode(500, new { error = "Failed to set CW pitch" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // --- RF GAIN ---
        public class RfGainRequest { public int Value { get; set; } = 255; }

        [HttpPost("rfgain/{receiver}")]
        public async Task<IActionResult> SetRfGain(string receiver, [FromBody] RfGainRequest request)
        {
            if (request.Value < 0 || request.Value > 255)
                return BadRequest(new { error = "RF Gain must be 0–255" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"RG{VfoP1Outgoing(receiver)}{request.Value:D3};", "WebUI", CancellationToken.None);
                if (VfoIsB(receiver)) _radioStateService.RfGainB = request.Value;
                else                  _radioStateService.RfGainA = request.Value;
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting RF gain");
                return StatusCode(500, new { error = "Failed to set RF gain" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // --- SQUELCH ---
        public class SquelchRequest { public int Value { get; set; } = 0; }

        [HttpPost("squelch/{receiver}")]
        public async Task<IActionResult> SetSquelch(string receiver, [FromBody] SquelchRequest request)
        {
            if (request.Value < 0 || request.Value > 255)
                return BadRequest(new { error = "Squelch must be 0–255" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"SQ{VfoP1Outgoing(receiver)}{request.Value:D3};", "WebUI", CancellationToken.None);
                if (VfoIsB(receiver)) _radioStateService.SquelchB = request.Value;
                else                  _radioStateService.SquelchA = request.Value;
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting squelch");
                return StatusCode(500, new { error = "Failed to set squelch" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // --- MONITOR ON/OFF ---
        public class MonitorOnRequest { public bool On { get; set; } }

        [HttpPost("monitoron")]
        public async Task<IActionResult> SetMonitorOn([FromBody] MonitorOnRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync(request.On ? "ML0001;" : "ML0000;", "WebUI", CancellationToken.None);
                _radioStateService.MonitorOn = request.On;
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting monitor on/off");
                return StatusCode(500, new { error = "Failed to set monitor" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // --- MONITOR LEVEL ---
        public class MonitorLevelRequest { public int Level { get; set; } = 0; }

        [HttpPost("monitorlevel/{receiver}")]
        public async Task<IActionResult> SetMonitorLevel(string receiver, [FromBody] MonitorLevelRequest request)
        {
            if (request.Level < 0 || request.Level > 100)
                return BadRequest(new { error = "Monitor level must be 0–100" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                var vfo = receiver.ToUpper() == "A" ? "0" : "1";
                await _catClient.SendCommandAsync($"ML{vfo}{request.Level:D3};", "WebUI", CancellationToken.None);
                if (vfo == "0") _radioStateService.MonitorLevelA = request.Level;
                else            _radioStateService.MonitorLevelB = request.Level;
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting monitor level");
                return StatusCode(500, new { error = "Failed to set monitor level" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // --- CONNECT / DISCONNECT ---
        [HttpPost("connect")]
        public async Task<IActionResult> Connect()
        {
            try
            {
                await _radioInitService.InitializeRadioAsync();
                AppStatus.InitializationStatus = "complete";
                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual connect failed");
                AppStatus.InitializationStatus = "error";
                return Ok(new { success = false, message = ex.Message });
            }
        }

        [HttpPost("disconnect")]
        public async Task<IActionResult> Disconnect()
        {
            try
            {
                await _catClient.DisconnectAsync();
                AppStatus.InitializationStatus = "disconnected";
                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual disconnect failed");
                return Ok(new { success = false, message = ex.Message });
            }
        }

        // --- VOX ---
        public class VoxRequest
        {
            public bool On { get; set; }
            public int Gain { get; set; } = 50;
            public int Delay { get; set; } = 50;
            public int AntiVoxGain { get; set; } = 50;
        }

        [HttpPost("vox")]
        public async Task<IActionResult> SetVox([FromBody] VoxRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"VX{(request.On ? 1 : 0)};", "WebUI", CancellationToken.None);
                _radioStateService.VoxOn = request.On;
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error setting VOX"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        public class VoxGainRequest { public int Gain { get; set; } = 50; }
        public class VoxDelayRequest { public int Delay { get; set; } = 50; }
        public class AntiVoxGainRequest { public int Gain { get; set; } = 50; }

        [HttpPost("vox/gain")]
        public async Task<IActionResult> SetVoxGain([FromBody] VoxGainRequest request)
        {
            if (request.Gain < 0 || request.Gain > 100)
                return BadRequest(new { error = "Gain 0–100" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"VG{request.Gain:D3};", "WebUI", CancellationToken.None);
                _radioStateService.VoxGain = request.Gain;
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error setting VOX gain"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("vox/delay")]
        public async Task<IActionResult> SetVoxDelay([FromBody] VoxDelayRequest request)
        {
            if (request.Delay < 0 || request.Delay > 2500)
                return BadRequest(new { error = "Delay 0–2500 ms" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"VD{request.Delay:D4};", "WebUI", CancellationToken.None);
                _radioStateService.VoxDelay = request.Delay;
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error setting VOX delay"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("vox/antivox")]
        public async Task<IActionResult> SetAntiVoxGain([FromBody] AntiVoxGainRequest request)
        {
            if (request.Gain < 0 || request.Gain > 100)
                return BadRequest(new { error = "Anti-VOX gain 0–100" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                // Anti-VOX is typically stored in menu — store locally only
                _radioStateService.AntiVoxGain = request.Gain;
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error setting anti-VOX gain"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        // --- FM REPEATER ---
        public class FmRepeaterRequest
        {
            public string ShiftDir { get; set; } = "0";
            public int OffsetHz { get; set; } = 600000;
            public string CtcssMode { get; set; } = "00";
            public string CtcssTone { get; set; } = "01";
        }

        [HttpPost("fmrepeater")]
        public async Task<IActionResult> SetFmRepeater([FromBody] FmRepeaterRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                if (new[] { "0", "1", "2", "3" }.Contains(request.ShiftDir))
                {
                    await _catClient.SendCommandAsync($"RS{request.ShiftDir};", "WebUI", CancellationToken.None);
                    _radioStateService.FmShiftDir = request.ShiftDir;
                }
                int offsetClamp = Math.Max(0, Math.Min(999999, request.OffsetHz));
                await _catClient.SendCommandAsync($"RO{offsetClamp:D6};", "WebUI", CancellationToken.None);
                _radioStateService.FmOffsetHz = offsetClamp;
                if (new[] { "00", "01", "02", "03" }.Contains(request.CtcssMode))
                {
                    await _catClient.SendCommandAsync($"CT{request.CtcssMode};", "WebUI", CancellationToken.None);
                    _radioStateService.CtcssMode = request.CtcssMode;
                }
                await _catClient.SendCommandAsync($"CN{request.CtcssTone};", "WebUI", CancellationToken.None);
                _radioStateService.CtcssTone = request.CtcssTone;
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error setting FM repeater"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        // --- CW KEYER ---
        public class CwSpeedRequest { public int Speed { get; set; } = 20; }
        public class CwBreakInRequest { public string Mode { get; set; } = "0"; }
        public class CwBreakInDelayRequest { public int DelayMs { get; set; } = 200; }

        [HttpPost("cw/speed")]
        public async Task<IActionResult> SetCwSpeed([FromBody] CwSpeedRequest request)
        {
            if (request.Speed < 4 || request.Speed > 60)
                return BadRequest(new { error = "CW speed 4–60 WPM" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"KS{request.Speed:D3};", "WebUI", CancellationToken.None);
                _radioStateService.CwSpeed = request.Speed;
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error setting CW speed"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        // CW Auto Zero In — fire-and-forget trigger. Radio nudges the VFO so
        // the received CW signal sits exactly at the operator's preferred CW
        // pitch (the value set via KP). Requested by IK2XRW Alessandro (#55).
        //
        // Yaesu format: ZI{P1};
        //   P1 = 0  MAIN band (= VFO A on dual-receiver, the only receiver
        //           on single-receiver radios)
        //   P1 = 1  SUB band (FTdx101 only; rejected on single-receiver)
        //
        // {vfo} URL segment selects which VFO:
        //   "a"      → P1=0 explicitly                    (VFO A button)
        //   "b"      → P1=1 on dual-receiver, P1=0 forced on single-receiver
        //              (which silently rejects P1=1 on P1=0-Fixed commands)
        //   "active" → follow VS / single-receiver fall-back. Used by the
        //              CW Keyer popup button so one click does the right
        //              thing without needing to know which side is in focus.
        [HttpPost("cw/zin/{vfo}")]
        public async Task<IActionResult> CwZeroIn(string vfo)
        {
            string v = (vfo ?? "").Trim().ToLowerInvariant();
            if (v != "a" && v != "b" && v != "active")
                return BadRequest(new { error = "VFO must be 'a', 'b', or 'active'" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                string p1;
                if (v == "active")
                {
                    p1 = _radioStateService.IsSingleReceiver
                        ? "0"
                        : (_radioStateService.ActiveVfo == 1 ? "1" : "0");
                }
                else
                {
                    // Explicit per-VFO targeting. On single-receiver radios
                    // P1=1 is silently ignored by the radio firmware, so the
                    // VFO B button is functionally a no-op there — that's
                    // accurate to how the hardware behaves.
                    p1 = _radioStateService.IsSingleReceiver
                        ? "0"
                        : (v == "b" ? "1" : "0");
                }
                await _catClient.SendCommandAsync($"ZI{p1};", "WebUI", CancellationToken.None);
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error sending CW Zero In"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("cw/breakin")]
        public async Task<IActionResult> SetCwBreakIn([FromBody] CwBreakInRequest request)
        {
            if (!new[] { "0", "1", "2" }.Contains(request.Mode))
                return BadRequest(new { error = "Break-in mode 0/1/2" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"BI{request.Mode};", "WebUI", CancellationToken.None);
                _radioStateService.CwBreakIn = request.Mode;
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error setting CW break-in"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("cw/breakindelay")]
        public async Task<IActionResult> SetCwBreakInDelay([FromBody] CwBreakInDelayRequest request)
        {
            if (request.DelayMs < 0 || request.DelayMs > 2500)
                return BadRequest(new { error = "Delay 0–2500 ms" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"SD{request.DelayMs:D4};", "WebUI", CancellationToken.None);
                _radioStateService.CwBreakInDelay = request.DelayMs;
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error setting CW break-in delay"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        public class CwMessageRequest { public string Message { get; set; } = ""; }

        [HttpPost("cw/send")]
        public async Task<IActionResult> SendCwMessage([FromBody] CwMessageRequest request)
        {
            if (string.IsNullOrEmpty(request.Message))
                return BadRequest(new { error = "Empty message" });
            var clean = new string(request.Message.ToUpper().Where(c =>
                (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                c == ' ' || c == '?' || c == '/' || c == '.' || c == ','
            ).Take(24).ToArray());
            if (string.IsNullOrEmpty(clean))
                return BadRequest(new { error = "No valid CW characters" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"KY {clean};", "WebUI", CancellationToken.None);
                return Ok(new { sent = clean });
            }
            catch (Exception ex) { _logger.LogError(ex, "Error sending CW message"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        // --- CW MESSAGES ---
        [HttpGet("cw/messages")]
        public async Task<IActionResult> GetCwMessages()
        {
            var settings = await _settingsService.GetSettingsAsync();
            return Ok(settings.CwMessages);
        }

        [HttpPost("cw/messages")]
        public async Task<IActionResult> SaveCwMessages([FromBody] List<string> messages)
        {
            if (messages == null || messages.Count != 5)
                return BadRequest(new { error = "Exactly 5 messages required" });
            var settings = await _settingsService.GetSettingsAsync();
            settings.CwMessages = messages.Select(m => m ?? "").Take(5).ToList();
            await _settingsService.SaveSettingsAsync(settings);
            return Ok(new { saved = true });
        }

        // -- AUDIO FILTER (LCUT/HCUT FREQ + SLOPE per mode class) ------------
        //
        // Yaesu stores LCUT FREQ, LCUT SLOPE, HCUT FREQ, HCUT SLOPE as
        // per-mode-class menu values (one set per SSB/AM/FM/DATA/RTTY/CW),
        // accessed via the EX command. The address differs per radio model
        // and is looked up from AudioFilterMapService (which reads the
        // wwwroot/data/audio-filter-ex-map.json table sourced from each
        // radio's CAT manual).
        //
        // The {vfo} URL segment ("a" or "b") tells the controller which VFO's
        // *current mode* to look up. Since the radio stores values per
        // mode class (not per VFO), the actual EX address depends only on
        // the mode, not the VFO. When both VFOs share a mode they share the
        // values — the response includes vfoBMode/vfoAMode so the UI can
        // surface that to the user.

        public class AudioFilterValueResult
        {
            public string? Code { get; set; }     // raw P4 code from the radio, e.g. "05" or "0"
            public int?    Hz { get; set; }       // freq in Hz, or null for slopes / OFF / unsupported
            public string? Label { get; set; }    // human label (slope: "6 dB/oct"; freq: "300 Hz" or "OFF")
            public bool    Supported { get; set; }
        }

        public class AudioFilterReadResponse
        {
            public string  RadioModel       { get; set; } = "";
            public string  Vfo              { get; set; } = "";
            public string? VfoMode          { get; set; }   // friendly mode of the requested VFO
            public string? OtherVfoMode     { get; set; }   // friendly mode of the *other* VFO
            public string? ModeClass        { get; set; }   // SSB / AM / FM / DATA / RTTY / CW
            public string? OtherModeClass   { get; set; }   // mode class of the *other* VFO
            public bool    OtherVfoShares   { get; set; }   // true if other VFO is in same mode class
            public AudioFilterValueResult LcutFreq  { get; set; } = new();
            public AudioFilterValueResult LcutSlope { get; set; } = new();
            public AudioFilterValueResult HcutFreq  { get; set; } = new();
            public AudioFilterValueResult HcutSlope { get; set; } = new();
        }

        public class AudioFilterSetRequest
        {
            public string Code { get; set; } = "";   // raw P4 code, formatted to the right digit count
        }

        [HttpGet("audiofilter/{vfo}")]
        public async Task<IActionResult> ReadAudioFilter(string vfo)
        {
            var v = (vfo ?? "").Trim().ToUpperInvariant();
            if (v != "A" && v != "B") return BadRequest(new { error = "Invalid VFO (must be 'a' or 'b')" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                var settings = await _settingsService.GetSettingsAsync();
                var radioModel = settings.RadioModel ?? "";

                var resp = new AudioFilterReadResponse { RadioModel = radioModel, Vfo = v };

                if (!_audioFilterMap.IsRadioSupported(radioModel))
                {
                    // Not in the map — return a response with everything unsupported
                    // so the UI can grey out cleanly.
                    return Ok(resp);
                }

                resp.VfoMode        = v == "B" ? _radioStateService.ModeB : _radioStateService.ModeA;
                resp.OtherVfoMode   = v == "B" ? _radioStateService.ModeA : _radioStateService.ModeB;
                resp.ModeClass      = AudioFilterMapService.ModeClassFor(resp.VfoMode);
                resp.OtherModeClass = AudioFilterMapService.ModeClassFor(resp.OtherVfoMode);
                resp.OtherVfoShares = resp.ModeClass != null && resp.ModeClass == resp.OtherModeClass;

                if (resp.ModeClass == null)
                {
                    // Mode not yet known (radio still initialising) — return supported=false everywhere.
                    return Ok(resp);
                }

                resp.LcutFreq  = await ReadOneAudioFilterValue(radioModel, resp.ModeClass, "lcutFreq");
                resp.LcutSlope = await ReadOneAudioFilterValue(radioModel, resp.ModeClass, "lcutSlope");
                resp.HcutFreq  = await ReadOneAudioFilterValue(radioModel, resp.ModeClass, "hcutFreq");
                resp.HcutSlope = await ReadOneAudioFilterValue(radioModel, resp.ModeClass, "hcutSlope");

                return Ok(resp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading audio filter for VFO {Vfo}", v);
                return StatusCode(500, new { error = "Failed to read audio filter values" });
            }
            finally { _requestSemaphore.Release(); }
        }

        private async Task<AudioFilterValueResult> ReadOneAudioFilterValue(
            string radioModel, string modeClass, string setting)
        {
            var result = new AudioFilterValueResult { Supported = false };

            var addr = _audioFilterMap.GetAddress(radioModel, modeClass, setting);
            if (addr == null) return result;

            var readCmd = _audioFilterMap.BuildReadCommand(radioModel, addr);
            var response = await _catClient.SendCommandAsync(readCmd, "WebUI", CancellationToken.None);
            var code = _audioFilterMap.ParseAnswerValueCode(radioModel, addr, response ?? "");
            if (code == null) return result;

            result.Supported = true;
            result.Code      = code;
            DecorateValueResult(result, setting, code);
            return result;
        }

        // Adds Hz / Label fields based on the raw code and which setting it is.
        private void DecorateValueResult(AudioFilterValueResult result, string setting, string code)
        {
            var ranges = _audioFilterMap.ValueRanges;
            if (setting == "lcutFreq" || setting == "hcutFreq")
            {
                var r = setting == "lcutFreq" ? ranges.LcutFreq : ranges.HcutFreq;
                if (code == r.Off)
                {
                    result.Label = "OFF";
                    result.Hz = null;
                }
                else if (int.TryParse(code, out int n))
                {
                    var hz = r.Min.Hz + (n - int.Parse(r.Min.Code)) * r.StepHz;
                    result.Hz = hz;
                    result.Label = $"{hz} Hz";
                }
            }
            else if (setting == "lcutSlope" || setting == "hcutSlope")
            {
                var opt = ranges.Slope.Options.FirstOrDefault(o => o.Code == code);
                result.Label = opt?.Label ?? code;
            }
        }

        [HttpPost("audiofilter/{vfo}/{setting}")]
        public async Task<IActionResult> WriteAudioFilter(
            string vfo, string setting, [FromBody] AudioFilterSetRequest request)
        {
            var v = (vfo ?? "").Trim().ToUpperInvariant();
            if (v != "A" && v != "B") return BadRequest(new { error = "Invalid VFO (must be 'a' or 'b')" });

            var allowedSettings = new[] { "lcutFreq", "lcutSlope", "hcutFreq", "hcutSlope" };
            if (!allowedSettings.Contains(setting))
                return BadRequest(new { error = $"Invalid setting (must be one of: {string.Join(", ", allowedSettings)})" });

            if (request == null || string.IsNullOrWhiteSpace(request.Code))
                return BadRequest(new { error = "Missing value code" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                var settings = await _settingsService.GetSettingsAsync();
                var radioModel = settings.RadioModel ?? "";

                if (!_audioFilterMap.IsRadioSupported(radioModel))
                    return BadRequest(new { error = $"Audio filter not supported on radio model '{radioModel}'" });

                var mode = v == "B" ? _radioStateService.ModeB : _radioStateService.ModeA;
                var modeClass = AudioFilterMapService.ModeClassFor(mode);
                if (modeClass == null)
                    return StatusCode(503, new { error = "Radio mode not yet known; try again shortly" });

                var addr = _audioFilterMap.GetAddress(radioModel, modeClass, setting);
                if (addr == null)
                    return BadRequest(new { error = $"{setting} is not exposed for {modeClass} mode on {radioModel}" });

                if (!ValidateValueCode(setting, request.Code))
                    return BadRequest(new { error = $"Invalid value code '{request.Code}' for {setting}" });

                var cmd = _audioFilterMap.BuildSetCommand(radioModel, addr, request.Code);
                await _catClient.SendCommandAsync(cmd, "WebUI", CancellationToken.None);

                // Re-read to confirm the radio accepted the write — EX writes
                // are documented as brittle, so we surface what the radio
                // actually stored rather than just trusting our request.
                var readCmd  = _audioFilterMap.BuildReadCommand(radioModel, addr);
                var response = await _catClient.SendCommandAsync(readCmd, "WebUI", CancellationToken.None);
                var actual   = _audioFilterMap.ParseAnswerValueCode(radioModel, addr, response ?? "");

                var result = new AudioFilterValueResult { Supported = true };
                if (actual != null)
                {
                    result.Code = actual;
                    DecorateValueResult(result, setting, actual);
                }
                else
                {
                    result.Code = request.Code;
                    DecorateValueResult(result, setting, request.Code);
                }

                return Ok(new { setting, modeClass, result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error writing audio filter {Setting} for VFO {Vfo}", setting, v);
                return StatusCode(500, new { error = "Failed to write audio filter value" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // Sanity-check the value code against the relevant value range.
        private bool ValidateValueCode(string setting, string code)
        {
            var ranges = _audioFilterMap.ValueRanges;
            if (setting == "lcutFreq" || setting == "hcutFreq")
            {
                var r = setting == "lcutFreq" ? ranges.LcutFreq : ranges.HcutFreq;
                if (code == r.Off) return true;
                if (!int.TryParse(code, out int n)) return false;
                if (!int.TryParse(r.Min.Code, out int min)) return false;
                if (!int.TryParse(r.Max.Code, out int max)) return false;
                return n >= min && n <= max && code.Length == r.Digits;
            }
            if (setting == "lcutSlope" || setting == "hcutSlope")
            {
                return ranges.Slope.Options.Any(o => o.Code == code);
            }
            return false;
        }
    }
}
