// Yaesu Web Control — where the FT-710's own scope row sits in frequency.
//
// The companion to Ft710ScopeFrame (the bytes); this half is the CAT side, so
// it lives in the main app only and is not linked into the SDR worker. The
// CENTER/CURSOR/FIX rules follow kd9taw/Nexus yaesu_wf.rs (GPL-3.0), whose
// placement was measured on an FT-710 by ON8ST.

namespace Yaesu_Web_Control.Services.Sdr;

/// <summary>
/// Where the FT-710's scope row sits in frequency, from the radio's SS
/// settings. The frame itself carries no frequency: its parameter block is
/// zeroes on this model. So the span comes from SS P2=5 and the placement
/// from SS P2=6, both read over CAT. Reads only; nothing here changes the
/// radio's scope.
/// </summary>
public static class Ft710ScopePlacement
{
    /// <summary>
    /// Span in Hz for an SS P2=5 code, or null. FT-710 CAT manual, SS P2=5:
    /// 0 1 kHz, 1 2 kHz, 2 5 kHz, 3 10 kHz, 4 20 kHz, 5 50 kHz, 6 100 kHz,
    /// 7 200 kHz, 8 500 kHz, 9 1 MHz.
    /// </summary>
    public static long? SpanHz(char code) => code switch
    {
        '0' => 1_000,
        '1' => 2_000,
        '2' => 5_000,
        '3' => 10_000,
        '4' => 20_000,
        '5' => 50_000,
        '6' => 100_000,
        '7' => 200_000,
        '8' => 500_000,
        '9' => 1_000_000,
        _   => null,
    };

    /// <summary>
    /// The outcome of reading the radio's scope settings: either a span the
    /// row can be drawn at, centred on the dial, or the reason it cannot.
    /// </summary>
    public sealed record Result(long? SpanHz, string? Problem)
    {
        public bool CanPlace => SpanHz is not null && Problem is null;
    }

    /// <summary>
    /// Decides whether frames can be placed. Only the CENTER modes (3DSS
    /// CENTER, W/F CENTER EXPAND and NORMAL) are symmetric about the dial.
    /// In CURSOR the window stays still while the dial moves across it, and
    /// in FIX it starts at a per-band edge that no CAT command reports, so
    /// frames in either would be drawn at a frequency that is not theirs.
    /// Showing nothing and saying why is better than a plausible wrong
    /// picture. The radio's scope mode is the operator's to change, so this
    /// never changes it.
    /// </summary>
    /// <param name="spanCode">P3 of the SS05 answer, or null if unanswered.</param>
    /// <param name="modeCode">P3 of the SS06 answer, or null if unanswered.</param>
    public static Result Resolve(char? spanCode, char? modeCode)
    {
        if (spanCode is null || modeCode is null)
            return new(null, "Waiting for the radio to report its scope span and mode over CAT.");

        long? span = SpanHz(spanCode.Value);
        if (span is null)
            return new(null, $"The radio reported a scope span code YWC does not know ('{spanCode}').");

        var (_, placement, _) = ScopeCommands.ParseMode(modeCode.Value);
        if (!IsKnownMode(modeCode.Value))
            return new(null, $"The radio reported a scope mode code YWC does not know ('{modeCode}').");

        return placement switch
        {
            ScopeCommands.PlacementCenter => new(span, null),
            ScopeCommands.PlacementCursor => new(null,
                "The radio's scope is in CURSOR mode. Set it to CENTER on the radio to see it here."),
            _ => new(null,
                "The radio's scope is in FIX mode. Set it to CENTER on the radio to see it here."),
        };
    }

    // FT-710 CAT manual, SS P2=6: 0,1,2 are 3DSS CENTER/CURSOR/FIX; 3,4 W/F
    // CENTER; 6,7 W/F CURSOR; 9,A W/F FIX. 5 and 8 are documented as "-".
    private static bool IsKnownMode(char c) =>
        c is '0' or '1' or '2' or '3' or '4' or '6' or '7' or '9' or 'A' or 'a';
}
