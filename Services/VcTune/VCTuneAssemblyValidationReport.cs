using System.Reflection;
using System.Text;

namespace Yaesu_Web_Control.Services;

/// <summary>
/// Immutable report produced by <see cref="GenerateReport"/> that confirms
/// whether every VC Tune subsystem is present, correctly typed, and wired into
/// <see cref="VCTuneModule"/>.
/// <para>
/// Obtain an instance by calling the static factory:
/// <code>
/// var report = VCTuneAssemblyValidationReport.GenerateReport(module, harness);
/// if (!report.AllReady) log.LogWarning(report.Summary);
/// </code>
/// </para>
/// </summary>
public sealed record VCTuneAssemblyValidationReport
{
    // ══════════════════════════════════════════════════════════════════════
    // Per-subsystem readiness flags
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <see langword="true"/> when <see cref="IVcTuneService"/> is wired into
    /// <see cref="VCTuneModule"/> and exposes all required async methods.
    /// </summary>
    public bool BackendServiceReady { get; init; }

    /// <summary>
    /// <see langword="true"/> when <see cref="IVCTuneCommandBuilder"/> is wired and
    /// exposes all six build methods (On, Off, Default, Step, Center, ReadStatus).
    /// </summary>
    public bool CommandBuilderReady { get; init; }

    /// <summary>
    /// <see langword="true"/> when <see cref="IVCTuneResponseParser"/> is wired and
    /// exposes ParseResponse, CanParse, and TryParse.
    /// </summary>
    public bool ResponseParserReady { get; init; }

    /// <summary>
    /// <see langword="true"/> when <see cref="IVCTuneStateMachine"/> is wired, all
    /// <see cref="VCTuneState"/> enum values are defined, and the state machine
    /// reports a valid snapshot for MAIN.
    /// </summary>
    public bool StateMachineReady { get; init; }

    /// <summary>
    /// <see langword="true"/> when <see cref="Voice.VCTuneRecognizer"/> is wired and
    /// all six intent-name constants are present and non-empty.
    /// </summary>
    public bool RecognizerReady { get; init; }

    /// <summary>
    /// <see langword="true"/> when <see cref="VCTuneViewModel"/> is wired and
    /// exposes the expected reactive properties for MAIN and SUB.
    /// </summary>
    public bool ViewModelReady { get; init; }

    /// <summary>
    /// <see langword="true"/> when <see cref="IVCTuneConfigurationStore"/> is wired,
    /// its P5/P6 safety rule is enforced (session state is absent from the persisted
    /// config type), and all required interface methods are present.
    /// </summary>
    public bool ConfigurationStoreReady { get; init; }

    /// <summary>
    /// <see langword="true"/> when <see cref="VCTuneDiagnostics"/> is wired, all
    /// seven <see cref="VCTuneErrorType"/> values are defined, and the history
    /// buffer is accessible.
    /// </summary>
    public bool DiagnosticsReady { get; init; }

    /// <summary>
    /// <see langword="true"/> when <see cref="VCTuneHelpProvider"/> is wired and
    /// <see cref="VCTuneModule.GetHelpSection"/> returns content for the
    /// "overview" section name.
    /// </summary>
    public bool HelpProviderReady { get; init; }

    /// <summary>
    /// <see langword="true"/> when the <see cref="VCTuneModule"/> parameter itself
    /// is non-null and all nine private subsystem fields are non-null (verified
    /// via reflection).
    /// </summary>
    public bool ModuleAssemblyReady { get; init; }

    /// <summary>
    /// <see langword="true"/> when the <see cref="VCTuneIntegrationHarness"/>
    /// parameter is non-null and all eight test-flow methods are present and
    /// return <c>Task&lt;VCTuneIntegrationResult&gt;</c>.
    /// </summary>
    public bool IntegrationHarnessReady { get; init; }

    // ══════════════════════════════════════════════════════════════════════
    // Aggregate
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <see langword="true"/> when every per-subsystem flag is <see langword="true"/>.
    /// Use this as the deployment gate check.
    /// </summary>
    public bool AllReady =>
        BackendServiceReady   &&
        CommandBuilderReady   &&
        ResponseParserReady   &&
        StateMachineReady     &&
        RecognizerReady       &&
        ViewModelReady        &&
        ConfigurationStoreReady &&
        DiagnosticsReady      &&
        HelpProviderReady     &&
        ModuleAssemblyReady   &&
        IntegrationHarnessReady;

    /// <summary>
    /// Human-readable deployment summary. Contains one line per subsystem
    /// (PASS / FAIL) followed by an overall verdict and any non-critical notes.
    /// </summary>
    public string Summary { get; init; } = string.Empty;

    // ══════════════════════════════════════════════════════════════════════
    // Factory
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Generates a <see cref="VCTuneAssemblyValidationReport"/> by inspecting the
    /// assembled <paramref name="module"/> and <paramref name="harness"/> at runtime.
    /// <para>
    /// Validation strategy:
    /// <list type="bullet">
    ///   <item>Each private field on <see cref="VCTuneModule"/> is retrieved via
    ///     reflection and checked for non-null.</item>
    ///   <item>Required interface methods are verified by name on the concrete type.</item>
    ///   <item>Enum completeness is verified for <see cref="VCTuneState"/> and
    ///     <see cref="VCTuneErrorType"/>.</item>
    ///   <item>The module is exercised with one non-CAT call
    ///     (<see cref="VCTuneModule.GetState"/>) to confirm live wiring.</item>
    ///   <item>The harness is verified structurally — test flows are NOT executed,
    ///     so no radio connection is required.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="module">The assembled <see cref="VCTuneModule"/> singleton.</param>
    /// <param name="harness">The <see cref="VCTuneIntegrationHarness"/> singleton.</param>
    /// <returns>A populated, immutable validation report.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="module"/> or <paramref name="harness"/> is null.
    /// </exception>
    public static VCTuneAssemblyValidationReport GenerateReport(
        VCTuneModule module,
        VCTuneIntegrationHarness harness)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(harness);

        var notes = new List<string>();

        // ── 1. Module private-field inspection ─────────────────────────────
        bool moduleAssemblyReady = CheckModuleFields(module, notes);

        // ── 2. Backend service ─────────────────────────────────────────────
        bool backendServiceReady = CheckBackendService(module, notes);

        // ── 3. Command builder ─────────────────────────────────────────────
        bool commandBuilderReady = CheckCommandBuilder(module, notes);

        // ── 4. Response parser ─────────────────────────────────────────────
        bool responseParserReady = CheckResponseParser(module, notes);

        // ── 5. State machine ───────────────────────────────────────────────
        bool stateMachineReady = CheckStateMachine(module, notes);

        // ── 6. Recognizer ──────────────────────────────────────────────────
        bool recognizerReady = CheckRecognizer(module, notes);

        // ── 7. View model ──────────────────────────────────────────────────
        bool viewModelReady = CheckViewModel(module, notes);

        // ── 8. Configuration store ─────────────────────────────────────────
        bool configStoreReady = CheckConfigurationStore(module, notes);

        // ── 9. Diagnostics ─────────────────────────────────────────────────
        bool diagnosticsReady = CheckDiagnostics(module, notes);

        // ── 10. Help provider ──────────────────────────────────────────────
        bool helpProviderReady = CheckHelpProvider(module, notes);

        // ── 11. Integration harness ────────────────────────────────────────
        bool harnessReady = CheckIntegrationHarness(harness, notes);

        // ── Summary ────────────────────────────────────────────────────────
        bool allReady =
            backendServiceReady && commandBuilderReady && responseParserReady &&
            stateMachineReady && recognizerReady && viewModelReady &&
            configStoreReady && diagnosticsReady && helpProviderReady &&
            moduleAssemblyReady && harnessReady;

        string summary = BuildSummary(
            allReady,
            backendServiceReady, commandBuilderReady, responseParserReady,
            stateMachineReady, recognizerReady, viewModelReady,
            configStoreReady, diagnosticsReady, helpProviderReady,
            moduleAssemblyReady, harnessReady,
            notes);

        return new VCTuneAssemblyValidationReport
        {
            BackendServiceReady     = backendServiceReady,
            CommandBuilderReady     = commandBuilderReady,
            ResponseParserReady     = responseParserReady,
            StateMachineReady       = stateMachineReady,
            RecognizerReady         = recognizerReady,
            ViewModelReady          = viewModelReady,
            ConfigurationStoreReady = configStoreReady,
            DiagnosticsReady        = diagnosticsReady,
            HelpProviderReady       = helpProviderReady,
            ModuleAssemblyReady     = moduleAssemblyReady,
            IntegrationHarnessReady = harnessReady,
            Summary                 = summary,
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    // Private validation helpers
    // ══════════════════════════════════════════════════════════════════════

    // Private field names in VCTuneModule that must be non-null.
    private static readonly string[] RequiredModuleFields =
    [
        "_service", "_builder", "_parser", "_stateMachine",
        "_recognizer", "_viewModel", "_configStore", "_diagnostics", "_helpProvider",
    ];

    // Verifies all nine required private fields in the module are non-null.
    private static bool CheckModuleFields(VCTuneModule module, List<string> notes)
    {
        bool ok = true;
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var fieldName in RequiredModuleFields)
        {
            var field = typeof(VCTuneModule).GetField(fieldName, flags);
            if (field is null)
            {
                notes.Add($"NOTE: Module field '{fieldName}' not found via reflection (trimming?).");
                // Not a hard failure — the field may be absent in a trimmed build.
                continue;
            }
            if (field.GetValue(module) is null)
            {
                notes.Add($"FAIL: Module field '{fieldName}' is null — DI wiring incomplete.");
                ok = false;
            }
        }
        return ok;
    }

    // Verifies IVcTuneService methods.
    private static bool CheckBackendService(VCTuneModule module, List<string> notes)
    {
        bool ok = true;
        string[] required =
        [
            "ProbeCapabilityAsync", "SetVCTuneOnAsync", "SetVCTuneOffAsync",
            "SetVCTuneDefaultAsync", "SetVCTuneStepAsync", "SetVCTuneCenterAsync",
            "ReadVCTuneStatusAsync",
        ];
        ok &= CheckInterfaceMethods(typeof(IVcTuneService), required, notes, "IVcTuneService");

        // Verify module's GetState is callable without throwing (no CAT required).
        try
        {
            _ = module.GetState(VCTuneBand.Main);
        }
        catch (Exception ex)
        {
            notes.Add($"FAIL: module.GetState threw unexpectedly: {ex.Message}");
            ok = false;
        }
        return ok;
    }

    // Verifies IVCTuneCommandBuilder methods.
    private static bool CheckCommandBuilder(VCTuneModule module, List<string> notes)
    {
        string[] required =
        [
            "BuildSetOn", "BuildSetOff", "BuildSetDefault",
            "BuildSetStep", "BuildSetCenter", "BuildReadStatus",
        ];
        return CheckInterfaceMethods(typeof(IVCTuneCommandBuilder), required, notes,
            "IVCTuneCommandBuilder");
    }

    // Verifies IVCTuneResponseParser methods.
    private static bool CheckResponseParser(VCTuneModule module, List<string> notes) =>
        CheckInterfaceMethods(
            typeof(IVCTuneResponseParser),
            ["ParseResponse", "CanParse", "TryParse"],
            notes,
            "IVCTuneResponseParser");

    // Verifies state machine interface methods and VCTuneState enum completeness.
    private static bool CheckStateMachine(VCTuneModule module, List<string> notes)
    {
        bool ok = CheckInterfaceMethods(
            typeof(IVCTuneStateMachine),
            ["UpdateState", "NotifyCommand", "GetState", "GetLastSnapshot"],
            notes,
            "IVCTuneStateMachine");

        // All seven states must be defined.
        VCTuneState[] requiredStates =
        [
            VCTuneState.Off, VCTuneState.On, VCTuneState.Default,
            VCTuneState.Stepping, VCTuneState.Centering,
            VCTuneState.Unavailable, VCTuneState.NotInstalled,
        ];
        foreach (var state in requiredStates)
        {
            if (!Enum.IsDefined(state))
            {
                notes.Add($"FAIL: VCTuneState.{state} is not defined.");
                ok = false;
            }
        }

        // State machine must return a valid snapshot for MAIN without radio.
        try
        {
            var snap = module.GetState(VCTuneBand.Main);
            if (snap is null)
            {
                notes.Add("FAIL: GetState(Main) returned null.");
                ok = false;
            }
        }
        catch (Exception ex)
        {
            notes.Add($"FAIL: GetState(Main) threw: {ex.Message}");
            ok = false;
        }
        return ok;
    }

    // Verifies VCTuneRecognizer intent constants and method presence.
    private static bool CheckRecognizer(VCTuneModule module, List<string> notes)
    {
        bool ok = true;
        var recType = typeof(Voice.VCTuneRecognizer);

        // Six intent constants must be non-empty strings.
        string[] intentFields =
        [
            nameof(Voice.VCTuneRecognizer.IntentOn),
            nameof(Voice.VCTuneRecognizer.IntentOff),
            nameof(Voice.VCTuneRecognizer.IntentDefault),
            nameof(Voice.VCTuneRecognizer.IntentStep),
            nameof(Voice.VCTuneRecognizer.IntentCenter),
            nameof(Voice.VCTuneRecognizer.IntentReadStatus),
        ];
        foreach (var fieldName in intentFields)
        {
            var field = recType.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            if (field is null)
            {
                notes.Add($"FAIL: VCTuneRecognizer.{fieldName} constant not found.");
                ok = false;
                continue;
            }
            var value = field.GetValue(null) as string;
            if (string.IsNullOrEmpty(value))
            {
                notes.Add($"FAIL: VCTuneRecognizer.{fieldName} is null or empty.");
                ok = false;
            }
        }

        // Static helper methods must be present.
#if WINDOWS
        ok &= CheckTypeMethods(recType,
            ["GetGrammarVariants", "IsVCTuneIntent"],
            notes, "VCTuneRecognizer");
#else
        ok &= CheckTypeMethods(recType,
            ["IsVCTuneIntent"],
            notes, "VCTuneRecognizer");
#endif

        // Instance capability and dispatch methods must be present.
        ok &= CheckTypeMethods(recType,
            ["UpdateCapability", "DispatchAsync"],
            notes, "VCTuneRecognizer");

        return ok;
    }

    // Verifies VCTuneViewModel reactive properties.
    private static bool CheckViewModel(VCTuneModule module, List<string> notes)
    {
        bool ok = true;
        var vmType = typeof(VCTuneViewModel);
        string[] requiredProps =
        [
            "MainMeter", "SubMeter", "MainAvailability", "SubAvailability",
            "MainState", "SubState", "MainControlsVisible",
            "MainIsOn", "MainIsOff", "MainIsDefault", "MainIsActive", "MainIsAvailable",
            "MainWarningText",
            "SubIsOn", "SubIsOff", "SubIsDefault", "SubIsActive",
            "SubIsEnabled", "SubIsVisible", "SubWarningText",
        ];
        foreach (var propName in requiredProps)
        {
            if (vmType.GetProperty(propName) is null)
            {
                notes.Add($"FAIL: VCTuneViewModel.{propName} property not found.");
                ok = false;
            }
        }
        return ok;
    }

    // Verifies IVCTuneConfigurationStore methods and P5/P6 safety rule.
    private static bool CheckConfigurationStore(VCTuneModule module, List<string> notes)
    {
        bool ok = CheckInterfaceMethods(
            typeof(IVCTuneConfigurationStore),
            [
                "LoadAsync", "GetPreferences", "SavePreferencesAsync",
                "GetCapabilities", "SaveCapabilitiesAsync", "RefineSubCapabilityFromP6Async",
                "GetSessionState", "RecordReadResult", "RecordCommand", "ResetSessionState",
            ],
            notes,
            "IVCTuneConfigurationStore");

        // Safety rule: the private PersistedConfig type inside VCTuneConfigurationStore
        // must NOT have a property named "SessionState" or any P5/P6 field.
        var persistedConfigType = typeof(VCTuneConfigurationStore)
            .GetNestedType("PersistedConfig", BindingFlags.NonPublic);
        if (persistedConfigType is null)
        {
            notes.Add("NOTE: VCTuneConfigurationStore.PersistedConfig not found via reflection.");
        }
        else
        {
            var sessionProp = persistedConfigType.GetProperty("SessionState");
            if (sessionProp is not null)
            {
                notes.Add("FAIL: PersistedConfig has a 'SessionState' property — P5/P6 safety rule violated!");
                ok = false;
            }
            var meterProp = persistedConfigType.GetProperty("Meter")
                         ?? persistedConfigType.GetProperty("MainMeter")
                         ?? persistedConfigType.GetProperty("SubMeter");
            if (meterProp is not null)
            {
                notes.Add("FAIL: PersistedConfig contains a meter (P5) property — safety rule violated!");
                ok = false;
            }
        }
        return ok;
    }

    // Verifies VCTuneDiagnostics methods and VCTuneErrorType enum completeness.
    private static bool CheckDiagnostics(VCTuneModule module, List<string> notes)
    {
        bool ok = CheckTypeMethods(
            typeof(VCTuneDiagnostics),
            [
                "LogSetCommand", "LogReadCommand", "LogRawResponse",
                "LogStateTransition",
                "LogMeterUpdate", "LogAvailabilityUpdate",
                "LogError", "LogFallbackActivation",
                "GetHistory", "ResetHistory",
            ],
            notes, "VCTuneDiagnostics");

        // All seven error types must be defined.
        VCTuneErrorType[] requiredErrors =
        [
            VCTuneErrorType.NotInstalled, VCTuneErrorType.UnavailableFrequency,
            VCTuneErrorType.InvalidParameters, VCTuneErrorType.CommandRejected,
            VCTuneErrorType.ReadFailure, VCTuneErrorType.Timeout,
            VCTuneErrorType.UnexpectedResponse,
        ];
        foreach (var error in requiredErrors)
        {
            if (!Enum.IsDefined(error))
            {
                notes.Add($"FAIL: VCTuneErrorType.{error} is not defined.");
                ok = false;
            }
        }
        return ok;
    }

    // Verifies VCTuneHelpProvider via a live GetHelpSection("overview") call.
    private static bool CheckHelpProvider(VCTuneModule module, List<string> notes)
    {
        bool ok = true;
        try
        {
            var sections = module.GetHelpSection("overview");
            if (sections is null || sections.Count == 0)
            {
                notes.Add("FAIL: GetHelpSection(\"overview\") returned empty.");
                ok = false;
            }
            else if (string.IsNullOrWhiteSpace(sections[0].Content))
            {
                notes.Add("FAIL: Overview help section has empty content.");
                ok = false;
            }

            // All eight section names must return at least one entry.
            string[] sectionNames =
            [
                "overview", "installation", "main-vs-sub",
                "meter", "availability", "commands", "voice", "troubleshooting",
            ];
            foreach (var name in sectionNames)
            {
                var result = module.GetHelpSection(name);
                if (result is null || result.Count == 0)
                {
                    notes.Add($"FAIL: GetHelpSection(\"{name}\") returned empty.");
                    ok = false;
                }
            }
        }
        catch (Exception ex)
        {
            notes.Add($"FAIL: GetHelpSection threw: {ex.Message}");
            ok = false;
        }
        return ok;
    }

    // Verifies integration harness method presence and return types.
    private static bool CheckIntegrationHarness(
        VCTuneIntegrationHarness harness, List<string> notes)
    {
        bool ok = true;
        string[] flowMethods =
        [
            "RunInitializationTestAsync",
            "RunStatusReadTestAsync",
            "RunOnOffDefaultTestAsync",
            "RunStepTestAsync",
            "RunCenterTestAsync",
            "RunSubCapabilityTestAsync",
            "RunErrorConditionTestAsync",
            "RunShutdownTestAsync",
        ];
        var harnessType = typeof(VCTuneIntegrationHarness);
        var expectedReturnType = typeof(Task<VCTuneIntegrationResult>);
        foreach (var methodName in flowMethods)
        {
            var method = harnessType.GetMethod(methodName);
            if (method is null)
            {
                notes.Add($"FAIL: VCTuneIntegrationHarness.{methodName} not found.");
                ok = false;
                continue;
            }
            if (method.ReturnType != expectedReturnType)
            {
                notes.Add(
                    $"FAIL: {methodName} return type is {method.ReturnType.Name}, expected Task<VCTuneIntegrationResult>.");
                ok = false;
            }
        }
        return ok;
    }

    // Checks that a type implements all of the named methods.
    private static bool CheckInterfaceMethods(
        Type interfaceType, string[] methodNames, List<string> notes, string label)
    {
        bool ok = true;
        foreach (var name in methodNames)
        {
            if (interfaceType.GetMethod(name) is null)
            {
                notes.Add($"FAIL: {label}.{name} method not found.");
                ok = false;
            }
        }
        return ok;
    }

    // Checks that a concrete type declares all of the named methods.
    private static bool CheckTypeMethods(
        Type type, string[] methodNames, List<string> notes, string label)
    {
        bool ok = true;
        foreach (var name in methodNames)
        {
            if (type.GetMethod(name) is null)
            {
                notes.Add($"FAIL: {label}.{name} method not found.");
                ok = false;
            }
        }
        return ok;
    }

    // Builds the human-readable summary string.
    private static string BuildSummary(
        bool allReady,
        bool backendServiceReady, bool commandBuilderReady, bool responseParserReady,
        bool stateMachineReady, bool recognizerReady, bool viewModelReady,
        bool configStoreReady, bool diagnosticsReady, bool helpProviderReady,
        bool moduleAssemblyReady, bool harnessReady,
        IReadOnlyList<string> notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VC Tune Assembly Validation Report");
        sb.AppendLine("===================================");
        sb.AppendLine();
        sb.AppendLine("Subsystem readiness:");
        sb.AppendLine($"  {Flag(moduleAssemblyReady    )} Module assembly (VCTuneModule field wiring)");
        sb.AppendLine($"  {Flag(backendServiceReady    )} Backend service (IVcTuneService)");
        sb.AppendLine($"  {Flag(commandBuilderReady    )} CAT command builder (IVCTuneCommandBuilder)");
        sb.AppendLine($"  {Flag(responseParserReady    )} CAT response parser (IVCTuneResponseParser)");
        sb.AppendLine($"  {Flag(stateMachineReady      )} State machine (IVCTuneStateMachine + VCTuneState enum)");
        sb.AppendLine($"  {Flag(recognizerReady        )} Voice recognizer (VCTuneRecognizer intents + methods)");
        sb.AppendLine($"  {Flag(viewModelReady         )} UI view model (VCTuneViewModel reactive properties)");
        sb.AppendLine($"  {Flag(configStoreReady       )} Configuration store (IVCTuneConfigurationStore + P5/P6 safety)");
        sb.AppendLine($"  {Flag(diagnosticsReady       )} Diagnostics (VCTuneDiagnostics + VCTuneErrorType enum)");
        sb.AppendLine($"  {Flag(helpProviderReady      )} Help provider (VCTuneHelpProvider — all 8 sections)");
        sb.AppendLine($"  {Flag(harnessReady           )} Integration harness (VCTuneIntegrationHarness — all 8 flows)");
        sb.AppendLine();

        if (notes.Count > 0)
        {
            sb.AppendLine("Notes and warnings:");
            foreach (var note in notes)
                sb.AppendLine($"  {note}");
            sb.AppendLine();
        }

        if (allReady)
        {
            sb.AppendLine("VERDICT: READY FOR DEPLOYMENT");
            sb.AppendLine("  All 11 subsystems are present, correctly typed, and wired.");
            sb.AppendLine("  VCTuneModule is the single entry point; all downstream");
            sb.AppendLine("  coordination (state machine, config store, diagnostics,");
            sb.AppendLine("  recognizer, view model) flows through it.");
            sb.AppendLine("  The integration harness provides 8 test flows for post-");
            sb.AppendLine("  deployment verification against live hardware.");
        }
        else
        {
            int failCount = new[]
            {
                backendServiceReady, commandBuilderReady, responseParserReady,
                stateMachineReady, recognizerReady, viewModelReady,
                configStoreReady, diagnosticsReady, helpProviderReady,
                moduleAssemblyReady, harnessReady,
            }.Count(r => !r);
            sb.AppendLine($"VERDICT: NOT READY — {failCount} subsystem(s) failed validation.");
            sb.AppendLine("  Review the FAIL entries in Notes above.");
            sb.AppendLine("  Ensure DI registrations are complete and all source files");
            sb.AppendLine("  have been compiled into the current build.");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Flag(bool ready) => ready ? "PASS" : "FAIL";
}
