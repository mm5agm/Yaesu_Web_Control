namespace Yaesu_Web_Control.Services;

/// <summary>
/// Per-model CAT encodings for CW break-in: the SD delay, and where Semi/Full
/// lives. Yaesu-specific tables, so this stays out of core/.
///
/// FTdx101MP/D, FTdx10 and FT-710 take SD as a two-digit STEP number
/// (00 = 30 ms, 01 = 50, 02 = 100, 03 = 150, 04 = 200, 05 = 250, 06 = 300,
/// then 100 ms steps to 33 = 3000). The FTDX3000 takes four-digit ms
/// (0030-3000). YWC sent four-digit ms to every model until 2026-10-02, which
/// the step-number radios ignored, and the two-digit answer never parsed.
///
/// BI is only 0 (off) / 1 (on) on every supported model. Semi vs Full is a
/// menu item, CW BK-IN TYPE (0 = SEMI, 1 = FULL). Its EX address is only
/// listed for a model once it has been read back on a real radio.
/// </summary>
public static class CwBreakInCodes
{
    public const int MinDelayMs = 30;
    public const int MaxDelayMs = 3000;

    private static readonly int[] Steps = BuildSteps();

    private static int[] BuildSteps()
    {
        var s = new List<int> { 30, 50, 100, 150, 200, 250 };
        for (int ms = 300; ms <= 3000; ms += 100) s.Add(ms);
        return s.ToArray();   // 34 entries, index 00-33
    }

    /// <summary>True when the model takes SD as a two-digit step number.</summary>
    public static bool UsesStepIndex(string? radioModel) => radioModel != "FTDX3000";

    /// <summary>The SD set command for a delay, snapped to what the radio can hold.</summary>
    public static string SetDelayCommand(string? radioModel, int delayMs)
    {
        int ms = Math.Clamp(delayMs, MinDelayMs, MaxDelayMs);
        return UsesStepIndex(radioModel)
            ? $"SD{NearestStep(ms):D2};"
            : $"SD{ms:D4};";
    }

    /// <summary>The delay the radio will actually hold for a requested value.</summary>
    public static int SnapDelayMs(string? radioModel, int delayMs)
    {
        int ms = Math.Clamp(delayMs, MinDelayMs, MaxDelayMs);
        return UsesStepIndex(radioModel) ? Steps[NearestStep(ms)] : ms;
    }

    /// <summary>
    /// Parses an SD answer to ms. The two forms differ in length, so the
    /// answer says which it is: "SD04;" is a step, "SD0700;" is ms.
    /// </summary>
    public static int? ParseDelayMs(string message)
    {
        var body = message.Trim().TrimEnd(';');
        if (!body.StartsWith("SD", StringComparison.Ordinal)) return null;
        var digits = body.Substring(2);
        if (!int.TryParse(digits, out int v)) return null;
        if (digits.Length == 2) return v >= 0 && v < Steps.Length ? Steps[v] : null;
        if (digits.Length == 4) return Math.Clamp(v, MinDelayMs, MaxDelayMs);
        return null;
    }

    /// <summary>
    /// EX address (the text after "EX") of CW BK-IN TYPE, or null where it
    /// hasn't been confirmed on a radio, in which case Full isn't offered.
    /// FTdx101MP/D: EX020111, read back 2026-10-02 on an FTdx101MP (answered
    /// 0 = SEMI, and its neighbour EX020112 answered 04, matching SD04).
    /// </summary>
    public static string? BkInTypeExAddress(string? radioModel) => radioModel switch
    {
        "FTdx101MP" or "FTdx101D" => "020111",
        _ => null,
    };

    public static bool SupportsFullBreakIn(string? radioModel) => BkInTypeExAddress(radioModel) != null;

    private static int NearestStep(int ms)
    {
        int best = 0;
        for (int i = 1; i < Steps.Length; i++)
            if (Math.Abs(Steps[i] - ms) < Math.Abs(Steps[best] - ms)) best = i;
        return best;
    }
}
