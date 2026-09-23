namespace Yaesu_Web_Control.Services;

// The transmit meters the radio's own TFT can show, as the choices an operator
// picks from, and how a choice maps to the CAT METER SW command (MS).
//
// This is what the radio's touch pop-up offers when the meter is touched. That
// pop-up cannot be opened over CAT - there is no command that touches the
// screen - so the Radio Display overlay draws its own copy from this table and
// sends MS, and the change then shows up in the captured picture.
//
// Two shapes, from the operating and CAT manuals in docs/manuals:
//
//   FTdx101MP / FTdx101D - two meters. OM p20: LEFT METER PO / COMP / TEMP,
//     RIGHT METER ALC / VDD / ID / SWR. CAT: MS P1 P2; with P1 0-2 and P2 0-3.
//     The CAT manual labels P1/P2 MAIN/SUB, but they are the left and right
//     meters: MS00 = PO + ALC (the radio's default) and MS13 = COMP + SWR were
//     both confirmed on Colin's front panel (2026-08-14/15).
//
//   FTdx10 / FT-710 - one meter. OM p16/p17: PO, COMP, ALC, VDD, ID, SWR.
//     CAT: MS P1 P2; with P1 0-5 and P2 fixed at 0.
//
// A model not listed here has no selection offered. The FTDX3000 has no TFT
// output for Radio Display to show, and its MS table is different again.
public static class FrontPanelMeters
{
    public sealed record Option(string Code, string Label);

    public sealed record Slot(string Id, string Label, IReadOnlyList<Option> Options);

    /// <param name="Slots">One per meter on the screen, in MS parameter order.</param>
    /// <param name="FixedSuffix">Digits sent after the slots (the FTdx10's P2 = 0).</param>
    public sealed record Layout(IReadOnlyList<Slot> Slots, string FixedSuffix);

    private static readonly Layout TwoMeters = new(
    [
        new Slot("left", "Left meter",
        [
            new Option("0", "PO"),
            new Option("1", "COMP"),
            new Option("2", "TEMP"),
        ]),
        new Slot("right", "Right meter",
        [
            new Option("0", "ALC"),
            new Option("1", "VDD"),
            new Option("2", "ID"),
            new Option("3", "SWR"),
        ]),
    ], "");

    private static readonly Layout OneMeter = new(
    [
        new Slot("meter", "Meter",
        [
            new Option("0", "PO"),
            new Option("1", "COMP"),
            new Option("2", "ALC"),
            new Option("3", "VDD"),
            new Option("4", "ID"),
            new Option("5", "SWR"),
        ]),
    ], "0");

    public static Layout? For(string? radioModel) => radioModel switch
    {
        "FTdx101MP" or "FTdx101D" => TwoMeters,
        "FTdx10" or "FT-710"      => OneMeter,
        _ => null
    };

    /// <summary>
    /// The MS digits for one code per slot, or null when the model offers no
    /// selection, the count is wrong, or a code is not one of that slot's options.
    /// </summary>
    public static string? BuildDigits(string? radioModel, IReadOnlyList<string>? codes)
    {
        var layout = For(radioModel);
        if (layout is null || codes is null || codes.Count != layout.Slots.Count) return null;
        for (int i = 0; i < codes.Count; i++)
            if (!layout.Slots[i].Options.Any(o => o.Code == codes[i])) return null;
        return string.Concat(codes) + layout.FixedSuffix;
    }

    /// <summary>
    /// One code per slot read back out of MS digits (as the radio reports them),
    /// or null for a slot whose digit is missing or not an option there.
    /// </summary>
    public static IReadOnlyList<string?> ParseDigits(string? radioModel, string? digits)
    {
        var layout = For(radioModel);
        if (layout is null) return [];
        var result = new string?[layout.Slots.Count];
        for (int i = 0; i < result.Length; i++)
        {
            if (digits is null || i >= digits.Length) continue;
            var code = digits[i].ToString();
            if (layout.Slots[i].Options.Any(o => o.Code == code)) result[i] = code;
        }
        return result;
    }
}
