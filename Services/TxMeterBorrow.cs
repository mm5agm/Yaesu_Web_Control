namespace Yaesu_Web_Control.Services;

/// <summary>
/// Which front-panel meter pair the FTdx101MP/D poller borrows while
/// transmitting, so RM0 can return SWR on its right-hand side.
///
/// MS digits are MAIN then SUB: MAIN 0 = POW, 1 = COMP, 2 = TEMP;
/// SUB 0 = ALC, 1 = VDD, 2 = ID, 3 = SWR. SWR is always on the SUB side.
///
/// In voice modes the MAIN side is COMP, which YWC shows as the compression
/// gauge. In CW, RTTY and the data modes the speech processor does nothing,
/// so COMP means nothing there. Bruce VK2RT's videos (2026-10-01) showed the
/// radio's own COMP needle pinned past full scale in DATA-L while YWC held
/// MS13, which on the radio's panel looked like PO at full scale. In those
/// modes YWC borrows POW + SWR (MS03) instead, which is what an operator
/// would want to see on the panel anyway.
/// </summary>
public static class TxMeterBorrow
{
    public const string CompAndSwr = "13";
    public const string PowerAndSwr = "03";

    /// <summary>The MS digits to borrow for a TX VFO in this mode.</summary>
    public static string SelectionFor(string? mode) =>
        UsesSpeechProcessor(mode) ? CompAndSwr : PowerAndSwr;

    /// <summary>
    /// True when RM0's left-hand value is a compression reading. False means
    /// it is PO, which YWC already reads separately with RM5.
    /// </summary>
    public static bool LeftIsCompression(string selection) => selection == CompAndSwr;

    // Unknown or empty mode keeps the long-standing MS13 behaviour.
    private static bool UsesSpeechProcessor(string? mode) => mode switch
    {
        "LSB" or "USB" or "AM" or "AM-N" or "FM" or "FM-N" => true,
        "CW-U" or "CW-L" or "RTTY-L" or "RTTY-U" or "PSK"
            or "DATA-L" or "DATA-U" or "DATA-FM" or "DATA-FM-N" => false,
        _ => true,
    };
}
