using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Yaesu_Web_Control.Hubs;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Services.Voice
{
    /// <summary>
    /// Maps a recognised semantic intent + parameter dictionary (from the
    /// SRGS grammar's <c>out.intent</c> tags) to CAT commands sent via
    /// <see cref="ICatClient"/>. Voice commands target whichever VFO's mic
    /// button was pressed (v2, per-VFO voice) — see <see cref="_vfo"/>.
    /// FTdx10 / FT-710 are single-receiver, so the target always collapses
    /// to A there (<see cref="RadioCapabilities.VfoP1"/> /
    /// <see cref="RadioCapabilities.VfoIsB"/> already enforce this).
    /// </summary>
    public sealed class IntentDispatcher
    {
        private readonly ILogger<IntentDispatcher> _logger;
        private readonly ICatClient _catClient;
        private readonly RadioStateService _state;
        private readonly ISettingsService _settings;
        private readonly IHubContext<RadioHub> _hub;

        public IntentDispatcher(
            ILogger<IntentDispatcher> logger,
            ICatClient catClient,
            RadioStateService state,
            ISettingsService settings,
            IHubContext<RadioHub> hub)
        {
            _logger = logger;
            _catClient = catClient;
            _state = state;
            _settings = settings;
            _hub = hub;
        }

        // §6.5 dry-run testing: when set, SendCommand() logs the CAT string
        // instead of sending it. AsyncLocal rather than a field/parameter
        // threaded through every intent handler -- it scopes cleanly to the
        // single DispatchAsync call tree without touching ~20 method
        // signatures, and doesn't leak into unrelated concurrent dispatches.
        private static readonly AsyncLocal<bool> _dryRun = new();

        // Which VFO this DispatchAsync call tree targets ("A" or "B") -- set
        // by whichever mic button the operator pressed (VoiceControlService
        // passes it through from StartListeningAsync). Same AsyncLocal
        // pattern as _dryRun, for the same reason: scopes to one dispatch
        // without threading a parameter through ~15 handler methods.
        private static readonly AsyncLocal<string?> _vfo = new();

        /// <summary>The targeted VFO for the in-flight dispatch ("A" or "B"); defaults to "A".</summary>
        private static string CurrentVfo => _vfo.Value ?? "A";

        /// <summary>P1 digit for per-VFO receive-control commands (AG/GT/PA/RA/BU/BD/SH/etc). See <see cref="RadioCapabilities.VfoP1"/>.</summary>
        private string VfoP1 => RadioCapabilities.VfoP1(_state.IsSingleReceiver, CurrentVfo);

        /// <summary>True when the targeted VFO's state should be written to the *B fields. See <see cref="RadioCapabilities.VfoIsB"/>.</summary>
        private bool VfoIsB => RadioCapabilities.VfoIsB(_state.IsSingleReceiver, _state.ActiveVfo, CurrentVfo);

        /// <summary>
        /// Dispatch a recognised intent. Returns true if the intent was
        /// known AND successfully sent to the radio; false if the intent
        /// name is unknown or arguments are invalid. When <paramref name="dryRun"/>
        /// is true, intent matching and confirmation-phrase generation run
        /// exactly as normal but no CAT command is actually sent to the
        /// radio (§6.5 "Test this pack" dry run). Read-modify-write intents
        /// (SwapVFO on single-receiver radios, NudgeIfWidth) need a real CAT
        /// readback to compute their result and will report unsuccessful in
        /// dry-run mode -- a known, acceptable limitation of testing without
        /// a connected radio. <paramref name="vfo"/> is "A" or "B" -- which
        /// mic button was pressed; ignored (collapses to "A") on
        /// single-receiver radios.
        /// </summary>
        public async Task<DispatchResult> DispatchAsync(
            string intent,
            IReadOnlyDictionary<string, object> parameters,
            CancellationToken cancellationToken = default,
            bool dryRun = false,
            string vfo = "A")
        {
            _dryRun.Value = dryRun;
            _vfo.Value = vfo;
            _logger.LogInformation(
                "[IntentDispatcher] intent={Intent} params={@Params} dryRun={DryRun} vfo={Vfo}",
                intent, parameters, dryRun, vfo);

            try
            {
                switch (intent)
                {
                    case "SetFrequency":   return await SetFrequencyAsync(parameters, cancellationToken);
                    case "SetBand":        return await SetBandAsync(parameters, cancellationToken);
                    case "SetMode":        return await SetModeAsync(parameters, cancellationToken);
                    case "SetNudgeStep":      return await SetNudgeStepAsync(parameters, cancellationToken);
                    case "SwapVFO":           return await SwapVfoAsync(cancellationToken);
                    case "NudgeFrequency":    return await NudgeFrequencyAsync(parameters, cancellationToken);
                    case "BandUp":            return await BandStepAsync(up: true, cancellationToken);
                    case "BandDown":          return await BandStepAsync(up: false, cancellationToken);
                    case "Macro":             return await MacroAsync(parameters, cancellationToken);
                    case "StatusFrequency":   return StatusFrequency();
                    case "StatusMode":        return StatusMode();
                    case "StatusBand":        return StatusBand();
                    case "TxOn":              return await TxOnAsync(cancellationToken);
                    case "TxOff":             return await TxOffAsync(cancellationToken);
                    case "SplitOn":           return await SplitAsync(true, cancellationToken);
                    case "SplitOff":          return await SplitAsync(false, cancellationToken);
                    case "Help":              return Help();
                    case "NudgeIfWidth":      return await NudgeIfWidthAsync(parameters, cancellationToken);
                    case "SetAfGain":          return await SetAfGainAsync(parameters, cancellationToken);
                    case "SetAttenuator":     return await SetAttenuatorAsync(parameters, cancellationToken);
                    case "SetPreamp":         return await SetPreampAsync(parameters, cancellationToken);
                    case "SetAgc":            return await SetAgcAsync(parameters, cancellationToken);
                    default:
                        _logger.LogWarning("[IntentDispatcher] Unknown intent: {Intent}", intent);
                        return new DispatchResult(false, "Unknown command");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[IntentDispatcher] Failed to dispatch intent {Intent}", intent);
                return new DispatchResult(false, intent);
            }
        }

        // -- SetFrequency --------------------------------------------------

        private async Task<DispatchResult> SetFrequencyAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!TryGetLong(args, "hz", out var hz))
            {
                _logger.LogWarning("[Voice] SetFrequency missing/invalid hz parameter");
                return new DispatchResult(false, "Set frequency");
            }
            var phrase = $"Move to {FormatFrequencyForSpeech(hz)}";
            // 30 kHz - 75 MHz: same bounds as the HTTP SetFrequencyA endpoint.
            if (hz < 30_000 || hz > 75_000_000)
            {
                _logger.LogWarning("[Voice] SetFrequency {Hz} out of range", hz);
                return new DispatchResult(false, phrase);
            }
            var command = $"{(VfoIsB ? "FB" : "FA")}{hz:D9};";
            await SendCommand(command, ct);
            if (VfoIsB) _state.FrequencyB = hz; else _state.FrequencyA = hz;
            _logger.LogInformation("[Voice] SetFrequency -> {Hz} Hz (VFO {Vfo})", hz, CurrentVfo);
            return new DispatchResult(true, phrase);
        }

        // -- SetBand -------------------------------------------------------

        // Band-default frequencies: typical FT8/data hangout in each amateur
        // band, plus standard SSB calling frequencies for bands where FT8
        // isn't the obvious centre. These are intentional "good starting
        // point" values; user can fine-tune with NudgeFrequency or
        // SetFrequency afterwards.
        private static readonly Dictionary<long, long> BandDefaultsHz = new()
        {
            [160] =  1_840_000,
            [80]  =  3_573_000,
            [60]  =  5_357_000,
            [40]  =  7_074_000,
            [30]  = 10_136_000,
            [20]  = 14_074_000,
            [17]  = 18_100_000,
            [15]  = 21_074_000,
            [12]  = 24_915_000,
            [10]  = 28_074_000,
            [6]   = 50_313_000,
            [4]   = 70_154_000,
        };

        private async Task<DispatchResult> SetBandAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!TryGetLong(args, "metres", out var metres))
            {
                _logger.LogWarning("[Voice] SetBand missing/invalid metres parameter");
                return new DispatchResult(false, "Change band");
            }
            var phrase = $"Move to {metres} metres";
            if (!BandDefaultsHz.TryGetValue(metres, out var hz))
            {
                _logger.LogWarning("[Voice] SetBand: no default frequency for {Metres}m", metres);
                return new DispatchResult(false, phrase);
            }
            var command = $"{(VfoIsB ? "FB" : "FA")}{hz:D9};";
            await SendCommand(command, ct);
            if (VfoIsB) _state.FrequencyB = hz; else _state.FrequencyA = hz;
            _logger.LogInformation("[Voice] SetBand -> {Metres}m -> {Hz} Hz (VFO {Vfo})", metres, hz, CurrentVfo);
            return new DispatchResult(true, phrase);
        }

        // -- SetMode -------------------------------------------------------

        private async Task<DispatchResult> SetModeAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!args.TryGetValue("mode", out var modeObj) || modeObj is not string mode)
            {
                _logger.LogWarning("[Voice] SetMode missing/invalid mode parameter");
                return new DispatchResult(false, "Set mode");
            }
            // CatCommands.FormatMode handles the Yaesu-mode-string -> MD code
            // translation.
            var command = CatCommands.FormatMode(mode, isSubVfo: VfoIsB);
            await SendCommand(command, ct);
            if (VfoIsB) _state.ModeB = mode; else _state.ModeA = mode;
            _logger.LogInformation("[Voice] SetMode -> {Mode} (VFO {Vfo})", mode, CurrentVfo);
            return new DispatchResult(true, $"Mode {ModeForSpeech(mode)}");
        }

        // -- SwapVFO -------------------------------------------------------

        private async Task<DispatchResult> SwapVfoAsync(CancellationToken ct)
        {
            var settings = await _settings.GetSettingsAsync();
            var isSingleReceiver = RadioCapabilities.IsSingleReceiver(settings.RadioModel);
            const string phrase = "Swap V F O";

            if (isSingleReceiver)
            {
                // Single-receiver radios (FTdx10/FT-710): no atomic SV;
                // command. Fake the swap by reading both stored frequencies
                // and writing them back swapped. Simpler than toggling VS,
                // and matches what the radio actually does when the user
                // presses A/B on the front panel.
                var faRaw = await SendCommand("FA;", ct);
                var fbRaw = await SendCommand("FB;", ct);
                if (!TryParseFreqResponse(faRaw, "FA", out var fa) ||
                    !TryParseFreqResponse(fbRaw, "FB", out var fb))
                {
                    _logger.LogWarning("[Voice] SwapVFO: couldn't parse FA/FB readback");
                    return new DispatchResult(false, phrase);
                }
                await SendCommand($"FA{fb:D9};", ct);
                await SendCommand($"FB{fa:D9};", ct);
                _state.FrequencyA = fb;
                _state.FrequencyB = fa;
                _logger.LogInformation("[Voice] SwapVFO (fake) -> A={A}, B={B}", fb, fa);
                return new DispatchResult(true, phrase);
            }
            else
            {
                // Dual-receiver (FTdx101): atomic SV; — the radio handles the
                // swap and broadcasts new FA/FB via auto-info.
                await SendCommand("SV;", ct);
                _logger.LogInformation("[Voice] SwapVFO (SV;)");
                return new DispatchResult(true, phrase);
            }
        }

        // -- SetNudgeStep --------------------------------------------------

        private static readonly long[] _validNudgeSteps = [10, 100, 1_000, 10_000, 100_000];

        private async Task<DispatchResult> SetNudgeStepAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!TryGetLong(args, "step", out var step) || !_validNudgeSteps.Contains(step))
                return new DispatchResult(false, "Set step size");

            var settings = await _settings.GetSettingsAsync();
            var isB = VfoIsB;
            if (isB) settings.VoiceNudgeStepHzB = step; else settings.VoiceNudgeStepHzA = step;
            await _settings.SaveSettingsAsync(settings);
            await _hub.Clients.All.SendAsync("RadioStateUpdate",
                new { property = isB ? "VoiceNudgeStepHzB" : "VoiceNudgeStepHzA", value = step }, ct);

            var label = step switch
            {
                10      => "ten hertz",
                100     => "one hundred hertz",
                1_000   => "one kilohertz",
                10_000  => "ten kilohertz",
                100_000 => "one hundred kilohertz",
                _       => $"{step} hertz",
            };
            _logger.LogInformation("[Voice] SetNudgeStep -> {Step} Hz", step);
            return new DispatchResult(true, $"Step size {label}");
        }

        // -- NudgeFrequency ------------------------------------------------

        private async Task<DispatchResult> NudgeFrequencyAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!TryGetLong(args, "direction", out var direction) || (direction != 1 && direction != -1))
            {
                _logger.LogWarning("[Voice] NudgeFrequency invalid direction");
                return new DispatchResult(false, "Tune");
            }
            var phrase = direction > 0 ? "Tune up" : "Tune down";
            var settings = await _settings.GetSettingsAsync();
            var isB = VfoIsB;
            var stepHzSetting = isB ? settings.VoiceNudgeStepHzB : settings.VoiceNudgeStepHzA;
            var stepHz = stepHzSetting > 0 ? stepHzSetting : 10_000;
            var current = isB ? _state.FrequencyB : _state.FrequencyA;
            var next = current + direction * stepHz;
            if (next < 30_000 || next > 75_000_000)
            {
                _logger.LogWarning("[Voice] NudgeFrequency would go out of range ({Next})", next);
                return new DispatchResult(false, phrase);
            }
            await SendCommand($"{(isB ? "FB" : "FA")}{next:D9};", ct);
            if (isB) _state.FrequencyB = next; else _state.FrequencyA = next;
            _logger.LogInformation("[Voice] NudgeFrequency {Dir} -> {Hz} Hz (VFO {Vfo})",
                direction > 0 ? "up" : "down", next, CurrentVfo);
            return new DispatchResult(true, phrase);
        }

        // -- BandUp / BandDown ---------------------------------------------

        private async Task<DispatchResult> BandStepAsync(bool up, CancellationToken ct)
        {
            var phrase = up ? "Band up" : "Band down";
            // BU/BD require a P1 band selector (0=MAIN, 1=SUB) per the
            // FTdx101MP CAT manual -- a bare "BU;"/"BD;" is malformed and
            // the radio silently ignores it.
            var command = (up ? "BU" : "BD") + VfoP1 + ";";
            await SendCommand(command, ct);
            _logger.LogInformation("[Voice] {Phrase} (VFO {Vfo})", phrase, CurrentVfo);
            return new DispatchResult(true, phrase);
        }

        // -- Macro ---------------------------------------------------------

        private async Task<DispatchResult> MacroAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            var name = args.TryGetValue("macroName", out var n) ? n?.ToString() ?? "Macro" : "Macro";
            if (!args.TryGetValue("macroCat", out var c) || c is not string cat || string.IsNullOrWhiteSpace(cat))
            {
                _logger.LogWarning("[Voice] Macro '{Name}' has no CAT string", name);
                return new DispatchResult(false, name);
            }
            // Split on ';' to support multi-command macros like "NR01;NB01;"
            foreach (var seg in cat.Split(';', StringSplitOptions.RemoveEmptyEntries))
                await SendCommand(seg + ";", ct);
            _logger.LogInformation("[Voice] Macro '{Name}' -> {Cat}", name, cat);
            return new DispatchResult(true, name);
        }

        // -- Status read-back (IsReadBack=true → no ", successful" appended) -----

        private DispatchResult StatusFrequency()
        {
            var hz = VfoIsB ? _state.FrequencyB : _state.FrequencyA;
            var phrase = FormatFrequencyForSpeech(hz);
            return new DispatchResult(true, phrase, IsReadBack: true);
        }

        private DispatchResult StatusMode()
        {
            var mode = VfoIsB ? _state.ModeB : _state.ModeA;
            var phrase = $"Mode {ModeForSpeech(mode ?? string.Empty)}";
            return new DispatchResult(true, phrase, IsReadBack: true);
        }

        private DispatchResult StatusBand()
        {
            var hz = VfoIsB ? _state.FrequencyB : _state.FrequencyA;
            var phrase = FrequencyToBandName(hz) is string band
                ? $"{band} metres"
                : "frequency not on a standard amateur band";
            return new DispatchResult(true, phrase, IsReadBack: true);
        }

        private static string? FrequencyToBandName(long hz) => hz switch
        {
            >= 1_800_000  and <= 2_000_000  => "one six zero",
            >= 3_500_000  and <= 4_000_000  => "eighty",
            >= 5_250_000  and <= 5_450_000  => "sixty",
            >= 7_000_000  and <= 7_300_000  => "forty",
            >= 10_100_000 and <= 10_150_000 => "thirty",
            >= 14_000_000 and <= 14_350_000 => "twenty",
            >= 18_068_000 and <= 18_168_000 => "seventeen",
            >= 21_000_000 and <= 21_450_000 => "fifteen",
            >= 24_890_000 and <= 24_990_000 => "twelve",
            >= 28_000_000 and <= 29_700_000 => "ten",
            >= 50_000_000 and <= 54_000_000 => "six",
            >= 70_000_000 and <= 71_000_000 => "four",
            _ => null,
        };

        private static DispatchResult Help() =>
            new(true,
                "You can say: set frequency, set mode, set band, band up or down, " +
                "tune up or down, swap V F O, or ask what frequency, what mode, " +
                "what band. Full list in the user manual.",
                IsReadBack: true);

        // -- TX / Split ----------------------------------------------------

        private async Task<DispatchResult> TxOnAsync(CancellationToken ct)
        {
            // TX P1: 0=CAT TX off (receive), 1=CAT TX on (key by CAT), 2=read-only
            // "keyed via PTT" answer. Same across FTdx101MP / FTdx10 / FT-710.
            // Keying therefore needs TX1; -- TX0; would just assert receive.
            await SendCommand("TX1;", ct);
            _state.IsTransmitting = true;
            _logger.LogInformation("[Voice] TxOn");
            return new DispatchResult(true, "Transmitting");
        }

        private async Task<DispatchResult> TxOffAsync(CancellationToken ct)
        {
            // TX0; = CAT TX off (return to receive). There is no standalone RX;
            // command in the FTdx101MP / FTdx10 / FT-710 CAT sets.
            await SendCommand("TX0;", ct);
            _state.IsTransmitting = false;
            _logger.LogInformation("[Voice] TxOff");
            return new DispatchResult(true, "Receive");
        }

        private async Task<DispatchResult> SplitAsync(bool on, CancellationToken ct)
        {
            await SendCommand(on ? "FT1;" : "FT0;", ct);
            _state.SplitMode = on ? 1 : 0;
            _logger.LogInformation("[Voice] Split {State}", on ? "on" : "off");
            return new DispatchResult(true, on ? "Split on" : "Split off");
        }

        // -- IF width / AF gain nudges (query → adjust → set) --------------

        private async Task<DispatchResult> NudgeIfWidthAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!TryGetLong(args, "direction", out var direction) || (direction != 1 && direction != -1))
                return new DispatchResult(false, "Filter width");

            var p1 = VfoP1;
            var raw = await SendCommand($"SH{p1};", ct);
            if (!TryParseIntResponse(raw, "SH", out var current))
                return new DispatchResult(false, "Filter width");

            var next = Math.Clamp(current + (int)direction, 0, 40);
            await SendCommand($"SH{p1}0{next:D2};", ct);
            if (VfoIsB) _state.IfWidthB = next.ToString(); else _state.IfWidthA = next.ToString();
            _logger.LogInformation("[Voice] NudgeIfWidth {Dir} -> {Next} (VFO {Vfo})", direction > 0 ? "wider" : "narrower", next, CurrentVfo);
            return new DispatchResult(true, direction > 0 ? "Filter wider" : "Filter narrower");
        }

        private async Task<DispatchResult> SetAfGainAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            // Vocabulary keys are 0-100 (percentage). Map to 0-255 for the CAT command.
            if (!TryGetLong(args, "level", out var pct))
                return new DispatchResult(false, "A F gain");

            pct = Math.Clamp(pct, 0, 100);
            var catValue = (int)Math.Round(pct * 255.0 / 100.0);
            await SendCommand($"AG{VfoP1}{catValue:D3};", ct);
            if (VfoIsB) _state.AfGainB = catValue; else _state.AfGainA = catValue;
            _logger.LogInformation("[Voice] SetAfGain {Pct}% -> CAT {CatValue} (VFO {Vfo})", pct, catValue, CurrentVfo);
            return new DispatchResult(true, $"Audio gain {pct}");
        }

        // -- Attenuator / Preamp / AGC -------------------------------------

        private async Task<DispatchResult> SetAttenuatorAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!args.TryGetValue("level", out var lvlObj) || lvlObj is not string level)
                return new DispatchResult(false, "Attenuator");

            var code = level switch
            {
                "off" => "0",
                "6"   => "1",
                "12"  => "2",
                "18"  => "3",
                _     => null,
            };
            if (code == null) return new DispatchResult(false, "Attenuator");

            await SendCommand($"RA{VfoP1}{code};", ct);
            var attValue = level switch { "off" => "00", "6" => "06", "12" => "12", "18" => "18", _ => (string?)null };
            if (VfoIsB) _state.AttB = attValue ?? _state.AttB; else _state.AttA = attValue ?? _state.AttA;
            var phrase = level == "off" ? "Attenuator off" : $"Attenuator {level} decibels";
            _logger.LogInformation("[Voice] SetAttenuator -> {Level} (VFO {Vfo})", level, CurrentVfo);
            return new DispatchResult(true, phrase);
        }

        private async Task<DispatchResult> SetPreampAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!args.TryGetValue("level", out var lvlObj) || lvlObj is not string level)
                return new DispatchResult(false, "Preamp");

            var code = level switch
            {
                "off" => "0",
                "1"   => "1",
                "2"   => "2",
                _     => null,
            };
            if (code == null) return new DispatchResult(false, "Preamp");

            await SendCommand($"PA{VfoP1}{code};", ct);
            if (VfoIsB) _state.IpoB = code; else _state.IpoA = code;
            var phrase = level switch
            {
                "off" => "Preamp off",
                "1"   => "Preamp amp one",
                "2"   => "Preamp amp two",
                _     => "Preamp",
            };
            _logger.LogInformation("[Voice] SetPreamp -> {Level} (VFO {Vfo})", level, CurrentVfo);
            return new DispatchResult(true, phrase);
        }

        private async Task<DispatchResult> SetAgcAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!args.TryGetValue("speed", out var spObj) || spObj is not string speed)
                return new DispatchResult(false, "A G C");

            var code = speed switch
            {
                "off"  => "0",
                "fast" => "1",
                "mid"  => "2",
                "slow" => "3",
                "auto" => "4",
                _      => null,
            };
            if (code == null) return new DispatchResult(false, "A G C");

            await SendCommand($"GT{VfoP1}{code};", ct);
            if (VfoIsB) _state.AgcB = code; else _state.AgcA = code;
            _logger.LogInformation("[Voice] SetAgc -> {Speed} (VFO {Vfo})", speed, CurrentVfo);
            return new DispatchResult(true, $"A G C {speed}");
        }

        // -- CAT send, dry-run-aware ----------------------------------------

        /// <summary>
        /// Every intent handler routes its CAT traffic through here instead
        /// of calling _catClient directly, so the §6.5 dry-run flag has a
        /// single choke point. In dry-run mode a query (bare "XX;") still
        /// isn't sent -- there's no live radio guaranteed to be present
        /// during a pack test -- so read-modify-write intents return an
        /// empty response and report unsuccessful; see DispatchAsync's doc
        /// comment.
        /// </summary>
        private Task<string> SendCommand(string command, CancellationToken ct)
        {
            if (_dryRun.Value)
            {
                _logger.LogInformation("[Voice] DRY RUN -- would send {Command}", command);
                return Task.FromResult(string.Empty);
            }
            return _catClient.SendCommandAsync(command, "Voice", ct);
        }

        // -- helpers -------------------------------------------------------

        /// <summary>
        /// SRGS semantic values come back as boxed primitives — int when the
        /// grammar tag does integer math, double if any decimal slipped in,
        /// string for explicit string assignments. Normalise to long.
        /// </summary>
        private static bool TryGetLong(
            IReadOnlyDictionary<string, object> args, string key, out long value)
        {
            value = 0;
            if (!args.TryGetValue(key, out var obj) || obj == null) return false;
            try
            {
                value = Convert.ToInt64(obj);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseIntResponse(string? raw, string prefix, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var trimmed = raw.Trim().TrimEnd(';');
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            return int.TryParse(trimmed.AsSpan(prefix.Length), out value);
        }

        /// <summary>Parses "FA01407400000;" or "FA01407400000" into a long Hz value.</summary>
        private static bool TryParseFreqResponse(string? raw, string prefix, out long hz)
        {
            hz = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var trimmed = raw.Trim().TrimEnd(';');
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            return long.TryParse(trimmed.AsSpan(prefix.Length), out hz);
        }

        // ── Speech-formatting helpers for the spoken confirmation ─────────
        //
        // TTS handles raw numbers ("14.074 megahertz") inconsistently across
        // installed voices -- one voice says "fourteen point oh seven four",
        // another says "fourteen point seventy-four". We spell the value out
        // digit-by-digit so the spoken confirmation matches what the user
        // said (digit-by-digit fractional MHz is the YWC v1 grammar).

        private static string FormatFrequencyForSpeech(long hz)
        {
            var mhz     = hz / 1_000_000;
            var rem     = hz % 1_000_000;
            var fracKhz = rem / 1000;   // 0-999: kHz digits
            var fracHz  = rem % 1000;   // 0-999: Hz digits (often 0)

            if (fracKhz == 0 && fracHz == 0)
                return $"{NumberWord(mhz)} megahertz";

            var khzSpelled = string.Join(" ", fracKhz.ToString("D3").Select(c => DigitWord(c - '0')));
            if (fracHz == 0)
                return $"{NumberWord(mhz)} point {khzSpelled} megahertz";

            // Include the sub-kHz digits so "18.128010" reads as
            // "eighteen point one two eight zero one zero megahertz".
            var hzSpelled = string.Join(" ", fracHz.ToString("D3").Select(c => DigitWord(c - '0')));
            return $"{NumberWord(mhz)} point {khzSpelled} {hzSpelled} megahertz";
        }

        private static string DigitWord(int d) => d switch
        {
            0 => "zero", 1 => "one", 2 => "two", 3 => "three", 4 => "four",
            5 => "five", 6 => "six", 7 => "seven", 8 => "eight", 9 => "nine",
            _ => d.ToString()
        };

        // Whole-MHz number names, matching VoiceGrammar.MhzWholeChoices so
        // the confirmation echoes the same words the user said. Numbers not
        // in the grammar (e.g. 23, 25) just speak as themselves.
        private static string NumberWord(long n) => n switch
        {
            0  => "zero",     1  => "one",      2  => "two",      3  => "three",
            4  => "four",     5  => "five",     6  => "six",      7  => "seven",
            8  => "eight",    9  => "nine",     10 => "ten",      11 => "eleven",
            12 => "twelve",   13 => "thirteen", 14 => "fourteen", 15 => "fifteen",
            16 => "sixteen",  17 => "seventeen",18 => "eighteen", 19 => "nineteen",
            20 => "twenty",   21 => "twenty one", 24 => "twenty four",
            28 => "twenty eight", 29 => "twenty nine", 30 => "thirty",
            50 => "fifty",    51 => "fifty one", 52 => "fifty two",
            70 => "seventy",  71 => "seventy one",
            _  => n.ToString()
        };

        // Letter modes ("USB", "LSB", "AM", "FM", "CW-U" etc.) read more
        // clearly when spelled out letter-by-letter, same as the grammar
        // input form. "DATA-U" -> "data U", "RTTY-L" -> "R T T Y L".
        private static string ModeForSpeech(string mode) => mode switch
        {
            "USB"      => "U S B",
            "LSB"      => "L S B",
            "AM"       => "A M",
            "FM"       => "F M",
            "AM-N"     => "A M narrow",
            "FM-N"     => "F M narrow",
            "CW-U"     => "C W upper",
            "CW-L"     => "C W lower",
            "RTTY-U"   => "R T T Y upper",
            "RTTY-L"   => "R T T Y lower",
            "DATA-U"   => "data upper",
            "DATA-L"   => "data lower",
            "DATA-FM"  => "data F M",
            "DATA-FM-N"=> "data F M narrow",
            "PSK"      => "P S K",
            _          => mode
        };
    }

    /// <summary>
    /// Result of dispatching a voice intent. <c>Success</c> drives the
    /// spoken-confirmation status ("successful" vs "unsuccessful");
    /// <c>ConfirmationPhrase</c> is the human-readable description of what
    /// the command tried to do, with parameter values folded in
    /// (e.g. "Move to fourteen point zero seven four megahertz", "Mode U S B").
    /// </summary>
    /// <summary>
    /// <c>IsReadBack</c> = true for status queries and help — the phrase is spoken directly
    /// without appending ", successful". Also bypasses the VoiceSpokenConfirmationEnabled
    /// gate so status queries always get a spoken answer.
    /// </summary>
    public record DispatchResult(bool Success, string ConfirmationPhrase, bool IsReadBack = false);
}
