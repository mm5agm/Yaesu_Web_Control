namespace Yaesu_Web_Control.Services;

/// <summary>
/// What the radio's PROC knob sets. The FTdx101 manual (p.50): with the
/// PROC TYPE menu (OPERATION SETTING > TX AUDIO, CAT EX030301) on COMP, the
/// knob sets the speech processor level (PL) while the processor is on and
/// the AMC output level (AO) while it is off; on AMC it always sets AO.
/// Only the FTdx101MP/D have this confirmed, so elsewhere the knob stays PL.
/// </summary>
public static class AmcCodes
{
    /// <summary>EX address (the text after "EX") of PROC TYPE, or null where it isn't confirmed.</summary>
    public static string? ProcTypeExAddress(string? radioModel) => radioModel switch
    {
        "FTdx101MP" or "FTdx101D" => "030301",
        _ => null,
    };

    public static bool SupportsAmc(string? radioModel) => ProcTypeExAddress(radioModel) != null;

    /// <summary>AO{nnn}; to 1-100, or null.</summary>
    public static int? ParseAmcLevel(string message)
    {
        var body = message.Trim().TrimEnd(';');
        if (!body.StartsWith("AO", StringComparison.Ordinal) || body.Length != 5) return null;
        return int.TryParse(body.AsSpan(2), out int v) ? Math.Clamp(v, 1, 100) : null;
    }
}
