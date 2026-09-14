#if WINDOWS
using System.Speech.Recognition;
#endif
using Microsoft.Extensions.Logging;

namespace Yaesu_Web_Control.Services.Voice
{
    /// <summary>
    /// Recognizer-integration subsystem for VC Tune preselector voice commands.
    /// <para>
    /// This class has two responsibilities:
    /// <list type="number">
    ///   <item><term>Grammar building</term>
    ///     <description>
    ///       <see cref="GetGrammarVariants"/> returns the set of <see cref="GrammarBuilder"/>
    ///       alternatives that <c>VoiceGrammar.BuildEnGb()</c> adds to the root grammar.
    ///       Grammar building is pure and static — no instance state required.
    ///     </description>
    ///   </item>
    ///   <item><term>Intent dispatch</term>
    ///     <description>
    ///       <see cref="DispatchAsync"/> handles every VC Tune intent that
    ///       <c>IntentDispatcher</c> routes here after recognizing a phrase.
    ///       Capability and availability checks are performed before any
    ///       service call is made.
    ///     </description>
    ///   </item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Grammar-to-token mapping</b><br/>
    /// <list type="table">
    ///   <item><term><c>vc_band</c></term>
    ///     <description>int — 0 = MAIN, 1 = SUB. Absent when no receiver was specified; dispatcher defaults to MAIN.</description></item>
    ///   <item><term><c>vc_direction</c></term>
    ///     <description>int — 1 = plus / forward, −1 = minus / backward. Only present for Step commands.</description></item>
    ///   <item><term><c>vc_step</c></term>
    ///     <description>int 0–9 — step amount. Only present for Step commands.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Register as a singleton in DI:
    /// <c>services.AddSingleton&lt;VCTuneRecognizer&gt;();</c>
    /// </para>
    /// </summary>
    public sealed class VCTuneRecognizer
    {
        // ══════════════════════════════════════════════════════════════════════
        // Intent name constants — matched by IntentDispatcher's switch
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>Intent name for "VC Tune on [receiver]".</summary>
        public const string IntentOn = "vc_tune_on_intent";

        /// <summary>Intent name for "VC Tune off [receiver]".</summary>
        public const string IntentOff = "vc_tune_off_intent";

        /// <summary>Intent name for "VC Tune default / auto-tune [receiver]".</summary>
        public const string IntentDefault = "vc_tune_default_intent";

        /// <summary>Intent name for "VC Tune plus/minus N [receiver]".</summary>
        public const string IntentStep = "vc_tune_step_intent";

        /// <summary>Intent name for "VC Tune center [receiver]".</summary>
        public const string IntentCenter = "vc_tune_center_intent";

        /// <summary>Intent name for "VC Tune status / check [receiver]".</summary>
        public const string IntentReadStatus = "vc_tune_read_status_intent";

        // ══════════════════════════════════════════════════════════════════════
        // Semantic token key constants — populated by the SAPI grammar
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>Semantic key for the target receiver. Value: int 0 = MAIN, 1 = SUB.</summary>
        public const string KeyBand = "vc_band";

        /// <summary>Semantic key for step direction. Value: int 1 = plus, −1 = minus.</summary>
        public const string KeyDirection = "vc_direction";

        /// <summary>Semantic key for step amount. Value: int 0–9.</summary>
        public const string KeyStep = "vc_step";

        // ══════════════════════════════════════════════════════════════════════
        // Instance members
        // ══════════════════════════════════════════════════════════════════════

        private readonly IVcTuneService _vcTuneService;
        private readonly IVCTuneStateMachine _stateMachine;
        private readonly ISettingsService _settings;
        private readonly ILogger<VCTuneRecognizer> _logger;

        // Runtime P6 availability cache. Updated by UpdateCapability() when
        // a VT READ response arrives. Stored as raw int (VcTuneAvailability enum value)
        // so volatile assignment is atomic. 0 = NotInstalled (safe default).
        private volatile int _mainAvailabilityRaw = 0;
        private volatile int _subAvailabilityRaw = 0;

        /// <summary>
        /// Initialises a new <see cref="VCTuneRecognizer"/> with its required dependencies.
        /// </summary>
        public VCTuneRecognizer(
            IVcTuneService vcTuneService,
            IVCTuneStateMachine stateMachine,
            ISettingsService settings,
            ILogger<VCTuneRecognizer> logger)
        {
            _vcTuneService = vcTuneService;
            _stateMachine = stateMachine;
            _settings = settings;
            _logger = logger;
        }

        // ══════════════════════════════════════════════════════════════════════
        // Grammar building (Windows SAPI only)
        // ══════════════════════════════════════════════════════════════════════

#if WINDOWS
        /// <summary>
        /// Returns all VC Tune <see cref="GrammarBuilder"/> variants for inclusion in the
        /// SAPI grammar. This method is static and pure — it does not consult radio state.
        /// Availability checks are performed at dispatch time.
        /// <para>
        /// Callers should compile each variant individually using <c>new Grammar(variant)</c>
        /// before adding to the root <see cref="Choices"/>, so that a failing variant can be
        /// reported with a clear name rather than causing the entire grammar to fail silently.
        /// </para>
        /// </summary>
        /// <returns>Twelve <see cref="GrammarBuilder"/> instances — two per intent.</returns>
        public static IReadOnlyList<GrammarBuilder> GetGrammarVariants() =>
            new[]
            {
                BuildVCTuneOn_NoBand(),
                BuildVCTuneOn_WithBand(),
                BuildVCTuneOff_NoBand(),
                BuildVCTuneOff_WithBand(),
                BuildVCTuneDefault_NoBand(),
                BuildVCTuneDefault_WithBand(),
                BuildVCTuneStep_NoBand(),
                BuildVCTuneStep_WithBand(),
                BuildVCTuneCenter_NoBand(),
                BuildVCTuneCenter_WithBand(),
                BuildVCTuneReadStatus_NoBand(),
                BuildVCTuneReadStatus_WithBand(),
            };
#endif

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="intent"/> is one of the
        /// six VC Tune intent names. Used by <c>IntentDispatcher</c> to route quickly
        /// before calling <see cref="DispatchAsync"/>.
        /// </summary>
        public static bool IsVCTuneIntent(string intent) =>
            intent is IntentOn or IntentOff or IntentDefault
                   or IntentStep or IntentCenter or IntentReadStatus;

        // ══════════════════════════════════════════════════════════════════════
        // Capability tracking
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Updates the recognizer's cached P6 availability for the given receiver.
        /// Called by the VC Tune service (or state machine observer) whenever a VT READ
        /// response arrives so that subsequent dispatch calls use the latest known
        /// availability without re-reading the radio.
        /// <para>
        /// Thread-safe: the backing fields are <c>volatile int</c>.
        /// </para>
        /// </summary>
        /// <param name="band">The receiver whose availability changed.</param>
        /// <param name="availability">The new P6 availability value.</param>
        public void UpdateCapability(VCTuneBand band, VcTuneAvailability availability)
        {
            if (band == VCTuneBand.Main)
                _mainAvailabilityRaw = (int)availability;
            else
                _subAvailabilityRaw = (int)availability;

            _logger.LogDebug(
                "[VCTuneRecognizer] Capability updated: {Band} → {Availability}",
                band, availability);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Intent dispatch
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Dispatches a recognised VC Tune intent to the backend service.
        /// Performs model-level and runtime P6 availability checks before any
        /// service call, returning a user-friendly <see cref="DispatchResult"/>
        /// on failure instead of throwing.
        /// </summary>
        /// <param name="intent">
        /// One of the six <c>IntentXxx</c> constant values defined on this class.
        /// </param>
        /// <param name="parameters">
        /// Flattened semantic dictionary from SAPI. May contain:
        /// <list type="bullet">
        ///   <item><c>vc_band</c> (int) — absent when no receiver was specified.</item>
        ///   <item><c>vc_direction</c> (int) — required for Step commands.</item>
        ///   <item><c>vc_step</c> (int) — required for Step commands.</item>
        /// </list>
        /// </param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>
        /// A <see cref="DispatchResult"/> whose <c>ConfirmationPhrase</c> is suitable
        /// for TTS playback; <c>Success = false</c> when any guard rejects the command.
        /// </returns>
        public async Task<DispatchResult> DispatchAsync(
            string intent,
            IReadOnlyDictionary<string, object> parameters,
            CancellationToken ct = default)
        {
            var settings = await _settings.GetSettingsAsync();

            if (!RadioCapabilities.SupportsVCTuneMain(settings.RadioModel))
            {
                return Fail("V C Tune is not supported on this radio.");
            }

            VcTuneReceiver receiver = ExtractReceiver(parameters);

            // ── SUB-specific guards ──────────────────────────────────────────
            if (receiver == VcTuneReceiver.Sub)
            {
                if (!RadioCapabilities.SupportsVCTuneSubStatic(settings.RadioModel))
                    return Fail("V C Tune is not available on the S U B receiver of this radio model.");

                var subSnapshot = _stateMachine.GetLastSnapshot(VCTuneBand.Sub);
                if (subSnapshot.State == VCTuneState.NotInstalled)
                    return Fail("V C Tune is not installed on the S U B receiver.");
                if (subSnapshot.State == VCTuneState.Unavailable)
                    return Fail("V C Tune is unavailable at the current frequency on the S U B receiver.");
            }
            else
            {
                // ── MAIN availability guard ──────────────────────────────────
                var mainSnapshot = _stateMachine.GetLastSnapshot(VCTuneBand.Main);
                if (mainSnapshot.State == VCTuneState.Unavailable)
                    return Fail("V C Tune is unavailable at the current frequency.");
            }

            _logger.LogDebug(
                "[VCTuneRecognizer] Dispatching intent={Intent} receiver={Receiver}",
                intent, receiver);

            return intent switch
            {
                IntentOn         => await DispatchOnAsync(receiver, ct),
                IntentOff        => await DispatchOffAsync(receiver, ct),
                IntentDefault    => await DispatchDefaultAsync(receiver, ct),
                IntentStep       => await DispatchStepAsync(receiver, parameters, ct),
                IntentCenter     => await DispatchCenterAsync(receiver, ct),
                IntentReadStatus => await DispatchReadStatusAsync(receiver, ct),
                _                => Fail($"Unknown V C Tune intent: {intent}")
            };
        }

        // ── Individual intent handlers ────────────────────────────────────────

        /// <summary>Handles <see cref="IntentOn"/>.</summary>
        private async Task<DispatchResult> DispatchOnAsync(VcTuneReceiver receiver, CancellationToken ct)
        {
            string phrase = $"V C Tune on{ReceiverSuffix(receiver)}";
            var result = await _vcTuneService.SetVCTuneOnAsync(receiver, ct);
            if (!result.Success)
                _logger.LogWarning(
                    "[VCTuneRecognizer] SetVCTuneOnAsync failed: [{Category}] {Message}",
                    result.ErrorCategory, result.Message);
            return new DispatchResult(result.Success, phrase);
        }

        /// <summary>Handles <see cref="IntentOff"/>.</summary>
        private async Task<DispatchResult> DispatchOffAsync(VcTuneReceiver receiver, CancellationToken ct)
        {
            string phrase = $"V C Tune off{ReceiverSuffix(receiver)}";
            var result = await _vcTuneService.SetVCTuneOffAsync(receiver, ct);
            if (!result.Success)
                _logger.LogWarning(
                    "[VCTuneRecognizer] SetVCTuneOffAsync failed: [{Category}] {Message}",
                    result.ErrorCategory, result.Message);
            return new DispatchResult(result.Success, phrase);
        }

        /// <summary>Handles <see cref="IntentDefault"/>.</summary>
        private async Task<DispatchResult> DispatchDefaultAsync(VcTuneReceiver receiver, CancellationToken ct)
        {
            string phrase = $"V C Tune auto-tune{ReceiverSuffix(receiver)}";
            var result = await _vcTuneService.SetVCTuneDefaultAsync(receiver, ct);
            if (!result.Success)
                _logger.LogWarning(
                    "[VCTuneRecognizer] SetVCTuneDefaultAsync failed: [{Category}] {Message}",
                    result.ErrorCategory, result.Message);
            return new DispatchResult(result.Success, phrase);
        }

        /// <summary>Handles <see cref="IntentStep"/>.</summary>
        private async Task<DispatchResult> DispatchStepAsync(
            VcTuneReceiver receiver,
            IReadOnlyDictionary<string, object> parameters,
            CancellationToken ct)
        {
            if (!TryExtractDirection(parameters, out char dirChar, out string? dirError))
                return Fail(dirError ?? "Please specify plus or minus.");

            if (!TryExtractStep(parameters, out int step, out string? stepError))
                return Fail(stepError ?? "Please specify a step amount between zero and nine.");

            string dirWord = dirChar == '+' ? "plus" : "minus";
            string phrase = $"V C Tune step {dirWord} {step}{ReceiverSuffix(receiver)}";
            var result = await _vcTuneService.SetVCTuneStepAsync(receiver, dirChar, step, ct);
            if (!result.Success)
                _logger.LogWarning(
                    "[VCTuneRecognizer] SetVCTuneStepAsync failed: [{Category}] {Message}",
                    result.ErrorCategory, result.Message);
            return new DispatchResult(result.Success, phrase);
        }

        /// <summary>Handles <see cref="IntentCenter"/>.</summary>
        private async Task<DispatchResult> DispatchCenterAsync(VcTuneReceiver receiver, CancellationToken ct)
        {
            string phrase = $"V C Tune center{ReceiverSuffix(receiver)}";
            var result = await _vcTuneService.SetVCTuneCenterAsync(receiver, ct);
            if (!result.Success)
                _logger.LogWarning(
                    "[VCTuneRecognizer] SetVCTuneCenterAsync failed: [{Category}] {Message}",
                    result.ErrorCategory, result.Message);
            return new DispatchResult(result.Success, phrase);
        }

        /// <summary>Handles <see cref="IntentReadStatus"/>.</summary>
        private async Task<DispatchResult> DispatchReadStatusAsync(VcTuneReceiver receiver, CancellationToken ct)
        {
            string phrase = $"V C Tune status{ReceiverSuffix(receiver)}";
            var readResult = await _vcTuneService.ReadVCTuneStatusAsync(receiver, ct);
            if (!readResult.IsValid)
                return Fail(phrase);

            string stateWord = readResult.IsOn ? "on" : "off";
            string detail = readResult.Meter >= 0
                ? $", meter {readResult.Meter}"
                : string.Empty;
            return new DispatchResult(true, $"V C Tune is {stateWord}{detail}{ReceiverSuffix(receiver)}");
        }

        // ══════════════════════════════════════════════════════════════════════
        // Token extraction helpers
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Extracts the target receiver from <paramref name="parameters"/>.
        /// Returns <see cref="VcTuneReceiver.Main"/> when the key is absent (spoken command
        /// did not include a band specifier).
        /// </summary>
        private static VcTuneReceiver ExtractReceiver(IReadOnlyDictionary<string, object> parameters)
        {
            if (!parameters.TryGetValue(KeyBand, out var raw))
                return VcTuneReceiver.Main;
            try
            {
                return Convert.ToInt32(raw) == 1 ? VcTuneReceiver.Sub : VcTuneReceiver.Main;
            }
            catch
            {
                return VcTuneReceiver.Main;
            }
        }

        /// <summary>
        /// Extracts and validates the step direction from <paramref name="parameters"/>.
        /// </summary>
        /// <param name="parameters">The semantic parameter dictionary from SAPI.</param>
        /// <param name="dirChar">On success: '+' or '−'.</param>
        /// <param name="error">On failure: a user-facing message suitable for TTS.</param>
        /// <returns><see langword="true"/> when a valid direction was found.</returns>
        private static bool TryExtractDirection(
            IReadOnlyDictionary<string, object> parameters,
            out char dirChar,
            out string? error)
        {
            dirChar = '+';
            error = null;

            if (!parameters.TryGetValue(KeyDirection, out var raw))
            {
                error = "Please specify plus or minus.";
                return false;
            }

            int v;
            try { v = Convert.ToInt32(raw); }
            catch
            {
                error = "Please specify plus or minus.";
                return false;
            }

            if (v == 1)  { dirChar = '+'; return true; }
            if (v == -1) { dirChar = '-'; return true; }

            error = $"Direction value {v} is not valid. Please specify plus or minus.";
            return false;
        }

        /// <summary>
        /// Extracts and validates the step amount from <paramref name="parameters"/>.
        /// </summary>
        /// <param name="parameters">The semantic parameter dictionary from SAPI.</param>
        /// <param name="step">On success: the validated step amount 0–9.</param>
        /// <param name="error">On failure: a user-facing message suitable for TTS.</param>
        /// <returns><see langword="true"/> when a valid step amount was found.</returns>
        private static bool TryExtractStep(
            IReadOnlyDictionary<string, object> parameters,
            out int step,
            out string? error)
        {
            step = 0;
            error = null;

            if (!parameters.TryGetValue(KeyStep, out var raw))
            {
                error = "Please specify a step amount between zero and nine.";
                return false;
            }

            int v;
            try { v = Convert.ToInt32(raw); }
            catch
            {
                error = "Please specify a step amount between zero and nine.";
                return false;
            }

            if (v is < 0 or > 9)
            {
                error = $"Step amount {v} is out of range. Please specify a value between zero and nine.";
                return false;
            }

            step = v;
            return true;
        }

        // ══════════════════════════════════════════════════════════════════════
        // Small helpers
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>Returns " on S U B" for SUB; empty string for MAIN.</summary>
        private static string ReceiverSuffix(VcTuneReceiver receiver) =>
            receiver == VcTuneReceiver.Sub ? " on S U B" : string.Empty;

        /// <summary>Returns a failed <see cref="DispatchResult"/> with the given phrase.</summary>
        private static DispatchResult Fail(string phrase) => new(false, phrase);

#if WINDOWS
        // ══════════════════════════════════════════════════════════════════════
        // Grammar builders (private static)
        // ══════════════════════════════════════════════════════════════════════
        //
        // Naming convention: Build<Intent>_<BandVariant>
        //   NoBand  — no vc_band token; dispatcher defaults to MAIN
        //   WithBand — vc_band token present; optional trailing "receiver" noun
        //
        // All builders follow the FLAT structure constraint documented in
        // VoiceGrammar.cs: no nested optional groups; a single trailing optional
        // (0..1 append at the very end) is safe per the SAPI comment there.

        // ── VC Tune On ────────────────────────────────────────────────────────

        /// <summary>Builds "v c tune on" → <see cref="IntentOn"/>.</summary>
        private static GrammarBuilder BuildVCTuneOn_NoBand()
        {
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", OnPhrases()));
            return gb;
        }

        /// <summary>Builds "v c tune on [main|sub] [receiver?]" → <see cref="IntentOn"/>.</summary>
        private static GrammarBuilder BuildVCTuneOn_WithBand()
        {
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", OnPhrases()));
            gb.Append(new SemanticResultKey(KeyBand, BandChoices()));
            gb.Append(new GrammarBuilder(new Choices("receiver")), 0, 1);
            return gb;
        }

        // ── VC Tune Off ───────────────────────────────────────────────────────

        /// <summary>Builds "v c tune off" → <see cref="IntentOff"/>.</summary>
        private static GrammarBuilder BuildVCTuneOff_NoBand()
        {
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", OffPhrases()));
            return gb;
        }

        /// <summary>Builds "v c tune off [main|sub] [receiver?]" → <see cref="IntentOff"/>.</summary>
        private static GrammarBuilder BuildVCTuneOff_WithBand()
        {
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", OffPhrases()));
            gb.Append(new SemanticResultKey(KeyBand, BandChoices()));
            gb.Append(new GrammarBuilder(new Choices("receiver")), 0, 1);
            return gb;
        }

        // ── VC Tune Default (auto-tune) ───────────────────────────────────────

        /// <summary>Builds "v c tune default / auto / auto tune" → <see cref="IntentDefault"/>.</summary>
        private static GrammarBuilder BuildVCTuneDefault_NoBand()
        {
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", DefaultPhrases()));
            return gb;
        }

        /// <summary>Builds "v c tune default [main|sub] [receiver?]" → <see cref="IntentDefault"/>.</summary>
        private static GrammarBuilder BuildVCTuneDefault_WithBand()
        {
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", DefaultPhrases()));
            gb.Append(new SemanticResultKey(KeyBand, BandChoices()));
            gb.Append(new GrammarBuilder(new Choices("receiver")), 0, 1);
            return gb;
        }

        // ── VC Tune Step ──────────────────────────────────────────────────────
        //
        // Step commands require direction AND amount.  The intent verb is just
        // "v c tune"; direction and step follow as separate semantic tokens.
        // Three consecutive SemanticResultKeys are used here.  If the SAPI
        // compiler rejects this, the Try() wrapper in VoiceGrammar will surface
        // a clear error at startup without crashing the engine.

        /// <summary>Builds "v c tune [plus|minus] [0–9]" → <see cref="IntentStep"/>.</summary>
        private static GrammarBuilder BuildVCTuneStep_NoBand()
        {
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", StepTriggerPhrase()));
            gb.Append(new SemanticResultKey(KeyDirection, DirectionChoices()));
            gb.Append(new SemanticResultKey(KeyStep, StepAmountChoices()));
            return gb;
        }

        /// <summary>Builds "v c tune [plus|minus] [0–9] [main|sub] [receiver?]" → <see cref="IntentStep"/>.</summary>
        private static GrammarBuilder BuildVCTuneStep_WithBand()
        {
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", StepTriggerPhrase()));
            gb.Append(new SemanticResultKey(KeyDirection, DirectionChoices()));
            gb.Append(new SemanticResultKey(KeyStep, StepAmountChoices()));
            gb.Append(new SemanticResultKey(KeyBand, BandChoices()));
            gb.Append(new GrammarBuilder(new Choices("receiver")), 0, 1);
            return gb;
        }

        // ── VC Tune Center ────────────────────────────────────────────────────

        /// <summary>Builds "v c tune center / centre" → <see cref="IntentCenter"/>.</summary>
        private static GrammarBuilder BuildVCTuneCenter_NoBand()
        {
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", CenterPhrases()));
            return gb;
        }

        /// <summary>Builds "v c tune center [main|sub] [receiver?]" → <see cref="IntentCenter"/>.</summary>
        private static GrammarBuilder BuildVCTuneCenter_WithBand()
        {
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", CenterPhrases()));
            gb.Append(new SemanticResultKey(KeyBand, BandChoices()));
            gb.Append(new GrammarBuilder(new Choices("receiver")), 0, 1);
            return gb;
        }

        // ── VC Tune Read Status ───────────────────────────────────────────────

        /// <summary>Builds "v c tune status / check" → <see cref="IntentReadStatus"/>.</summary>
        private static GrammarBuilder BuildVCTuneReadStatus_NoBand()
        {
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", ReadStatusPhrases()));
            return gb;
        }

        /// <summary>Builds "v c tune status [main|sub] [receiver?]" → <see cref="IntentReadStatus"/>.</summary>
        private static GrammarBuilder BuildVCTuneReadStatus_WithBand()
        {
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", ReadStatusPhrases()));
            gb.Append(new SemanticResultKey(KeyBand, BandChoices()));
            gb.Append(new GrammarBuilder(new Choices("receiver")), 0, 1);
            return gb;
        }

        // ══════════════════════════════════════════════════════════════════════
        // Shared Choices factories
        // Each factory returns a FRESH Choices on every call — reusing one Choices
        // across multiple GrammarBuilder.Append calls can trigger the SAPI
        // "rule reference not defined" compile error.
        // ══════════════════════════════════════════════════════════════════════

        private static Choices OnPhrases()
        {
            var c = new Choices();
            c.Add(new SemanticResultValue("v c tune on", IntentOn));
            return c;
        }

        private static Choices OffPhrases()
        {
            var c = new Choices();
            c.Add(new SemanticResultValue("v c tune off", IntentOff));
            return c;
        }

        private static Choices DefaultPhrases()
        {
            var c = new Choices();
            c.Add(new SemanticResultValue("v c tune default", IntentDefault));
            c.Add(new SemanticResultValue("v c tune auto", IntentDefault));
            c.Add(new SemanticResultValue("v c tune auto tune", IntentDefault));
            return c;
        }

        private static Choices StepTriggerPhrase()
        {
            var c = new Choices();
            c.Add(new SemanticResultValue("v c tune", IntentStep));
            return c;
        }

        private static Choices CenterPhrases()
        {
            var c = new Choices();
            c.Add(new SemanticResultValue("v c tune center", IntentCenter));
            c.Add(new SemanticResultValue("v c tune centre", IntentCenter));
            return c;
        }

        private static Choices ReadStatusPhrases()
        {
            var c = new Choices();
            c.Add(new SemanticResultValue("v c tune status", IntentReadStatus));
            c.Add(new SemanticResultValue("v c tune check", IntentReadStatus));
            return c;
        }

        /// <summary>Returns a fresh Choices for the receiver band token.</summary>
        private static Choices BandChoices()
        {
            var c = new Choices();
            c.Add(new SemanticResultValue("main", (int)VCTuneBand.Main));
            c.Add(new SemanticResultValue("sub", (int)VCTuneBand.Sub));
            return c;
        }

        /// <summary>Returns a fresh Choices for the step direction token.</summary>
        private static Choices DirectionChoices()
        {
            var c = new Choices();
            c.Add(new SemanticResultValue("plus", 1));
            c.Add(new SemanticResultValue("up", 1));
            c.Add(new SemanticResultValue("minus", -1));
            c.Add(new SemanticResultValue("down", -1));
            return c;
        }

        /// <summary>Returns a fresh Choices for the step amount token (0–9).</summary>
        private static Choices StepAmountChoices()
        {
            var c = new Choices();
            c.Add(new SemanticResultValue("zero", 0));
            c.Add(new SemanticResultValue("one",  1));
            c.Add(new SemanticResultValue("two",  2));
            c.Add(new SemanticResultValue("three",3));
            c.Add(new SemanticResultValue("four", 4));
            c.Add(new SemanticResultValue("five", 5));
            c.Add(new SemanticResultValue("six",  6));
            c.Add(new SemanticResultValue("seven",7));
            c.Add(new SemanticResultValue("eight",8));
            c.Add(new SemanticResultValue("nine", 9));
            return c;
        }
#endif
    }
}
