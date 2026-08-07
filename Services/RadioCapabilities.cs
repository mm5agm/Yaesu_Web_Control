namespace Yaesu_Web_Control.Services;

// Capability lookup for per-model behaviour differences. Currently used only
// for the dual- vs single-receiver UI decision (active-VFO greying-out and
// PTT placement on single-receiver radios), but the pattern scales: future
// per-model variations (4 m band availability, max TX power, roofing-filter
// availability) can hang off this same static class.
//
// See docs/decisions/0003-single-vs-dual-receiver-ui.md for the design
// rationale and Jacek SP3L's #34 report that drove it.
//
// Single-receiver is the safe default for unknown models — it applies the
// active/inactive UI restriction, which over-constrains an unfamiliar radio
// rather than letting it edit both VFOs' controls simultaneously when
// possibly only one set actually exists in the hardware.
public static class RadioCapabilities
{
    // Hardware revision IDs confirmed to reject VT CAT commands despite the
    // model supporting VC Tune in principle. The physical preselector works
    // from the front panel but is not exposed via CAT on these revisions.
    //
    // Confirmed 2026-07-01 by Colin MM5AGM on FTdx101MP ID0682:
    //   VT VCT 0,1,+,0; → ?;?;  (CAT error)
    //   FA; → correct frequency  (serial link otherwise functional)
    //   Firmware: MAIN V01-28 / DISPLAY V01-51 / DSP V01-20 (current / up to date)
    private static readonly HashSet<string> _vcTuneCatBlockedIds = ["0682"];
    /// <summary>
    /// True when the radio has two independent physical receiver chains
    /// (MAIN + SUB), each with its own set of RX controls addressable
    /// separately via CAT (P1=0 for MAIN, P1=1 for SUB on commands like
    /// PA, GT, RA, RG, NR, etc.).
    /// </summary>
    public static bool IsDualReceiver(string radioModel) => radioModel switch
    {
        // FTDX5000 (all variants — 5000/MP/D share the CAT command set) is a
        // true MAIN+SUB dual-receiver radio addressed by P1=0/1 exactly like
        // the FTdx101MP. See docs/design/ftdx5000-dual-receiver-plan.md.
        "FTdx101MP" or "FTdx101D" or "FTDX5000MP" or "FTDX5000D" => true,
        _ => false
    };

    /// <summary>
    /// True when the radio has a single physical receiver. VFO A and VFO B
    /// are frequency-and-mode memory slots through which the single
    /// receiver is steered; the radio stores per-VFO RX-control state but
    /// CAT commands always address whichever VFO is currently active
    /// (no P1 band parameter on the receiver-side commands).
    /// </summary>
    public static bool IsSingleReceiver(string radioModel) => !IsDualReceiver(radioModel);

    /// <summary>
    /// True when the radio has more than one antenna jack and YWC should
    /// expose a per-VFO antenna selector. Single-antenna radios get the
    /// selector hidden (Jacek SP3L #34, FTdx10 has one ANT jack — showing
    /// a selector for a control that does nothing is just visual noise).
    /// </summary>
    public static bool HasAntennaSelector(string radioModel) => radioModel switch
    {
        "FTdx10" or "FT-991A" => false,
        _                     => true
    };

    /// <summary>
    /// True when the radio has a Quick Memory Bank reachable over CAT via the
    /// QI (store) / QR (recall) opcode pair, so YWC can expose Store/Recall
    /// buttons next to the band selector. Every Yaesu model YWC supports has a
    /// QMB, and QI/QR are common across the modern Yaesu CAT reference (both are
    /// documented in the CAT manuals and are in scripts/probe's command sweep).
    ///
    /// NOTE (bench-gate): the exact store/recall behaviour — stack depth and
    /// whether QR cycles through the slots — differs across the line, so QI/QR
    /// should be confirmed against real hardware per model before we rely on it.
    /// Confirmed on: FTdx101MP (Colin MM5AGM, 2026-08-07 — QI/QR reach the rig
    /// and the display shows "QMB", with VM; returning to VFO). The rest remain
    /// enabled on the strength of the shared Yaesu CAT set but are not yet
    /// hardware-verified; narrow this list if any model rejects QI/QR.
    /// </summary>
    public static bool SupportsQmb(string radioModel) => radioModel switch
    {
        "FTdx101MP" or "FTdx101D" or "FTdx10" or "FT-710"
            or "FTDX3000" or "FTDX5000MP" or "FTDX5000D" or "FT-991A" => true,
        _ => false
    };

    /// <summary>
    /// True when the radio has a MAIN VC Tune preselector that can be
    /// controlled via the VT CAT command. Currently limited to the FTdx101
    /// family; all other supported models lack this hardware.
    /// </summary>
    public static bool SupportsVCTuneMain(string radioModel) =>
        radioModel is "FTdx101MP" or "FTdx101D";

    /// <summary>
    /// True when the radio model can optionally have a SUB VC Tune board
    /// fitted (static capability — hardware presence must still be confirmed
    /// at runtime via the P6 field in the VT CAT response).
    /// Only the FTdx101MP supports the SUB VC Tune option; the FTdx101D SUB
    /// receiver does not have a VC Tune capacitor.
    /// </summary>
    public static bool SupportsVCTuneSubStatic(string radioModel) =>
        radioModel == "FTdx101MP";

    /// <summary>
    /// True when the hardware revision reported by the ID; CAT command supports
    /// VC Tune control over CAT. Returns false for hardware revisions confirmed
    /// to reject VT frames (see <see cref="_vcTuneCatBlockedIds"/>).
    /// Also returns false for a null or empty ID (conservative default — ID is
    /// populated from the ID; response during init; until that arrives treat as
    /// not supported so no VT commands are sent prematurely).
    /// </summary>
    public static bool SupportsVCTuneCat(string? hardwareId) =>
        !string.IsNullOrEmpty(hardwareId) && !_vcTuneCatBlockedIds.Contains(hardwareId);

    /// <summary>
    /// Returns the P1 character for a per-VFO CAT command, given the
    /// user's (or voice command's) targeted receiver ("A" or "B"). On
    /// single-receiver radios always "0" -- the firmware hard-codes that
    /// position and rejects P1=1; on dual-receiver "0" for A, "1" for B.
    /// Shared by CatController (mouse/keyboard input) and IntentDispatcher
    /// (voice input) so the routing rule lives in exactly one place.
    /// </summary>
    public static string VfoP1(bool isSingleReceiver, string receiver) =>
        isSingleReceiver
            ? "0"
            : (receiver.Equals("B", StringComparison.OrdinalIgnoreCase) ? "1" : "0");

    /// <summary>
    /// Returns true if the per-VFO state write should target *B (vs *A) for
    /// a targeted receiver. On single-receiver radios the change always
    /// applies to whichever VFO is currently active -- the targeted panel
    /// is a hint, not an addressable target -- so this mirrors
    /// <paramref name="activeVfo"/> (0 = A, 1 = B). On dual-receiver radios
    /// the target wins outright.
    /// </summary>
    public static bool VfoIsB(bool isSingleReceiver, int activeVfo, string receiver) =>
        isSingleReceiver
            ? activeVfo == 1
            : receiver.Equals("B", StringComparison.OrdinalIgnoreCase);
}
