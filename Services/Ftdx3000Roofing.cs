namespace Yaesu_Web_Control.Services;

/// <summary>
/// FTDX3000 roofing-filter CAT code tables.
///
/// The FTDX3000 uses a different value space for the RF <b>set</b> parameter
/// (P2) than for the RF <b>read</b> answer (P3). Notably 600 Hz is set with 4
/// but reads back as 7, 300 Hz is set with 5 but reads back as 8, and while in
/// AUTO the radio reports the filter actually in circuit via codes 4/5/6/9/A.
/// Treating the read answer as if it were a set code (as the earlier inline
/// implementation did) mislabels 600 Hz / 300 Hz and desyncs the dropdown in
/// AUTO. Verified against the FTDX3000 CAT Operation Reference Book and
/// Hamlib's rigs/yaesu/ft3000.c roofing_filters table.
/// </summary>
public static class Ftdx3000Roofing
{
    /// <summary>Dropdown / RF set code (P2) -> display name.</summary>
    public static readonly IReadOnlyDictionary<string, string> SetCodeNames =
        new Dictionary<string, string>
        {
            ["0"] = "Auto", ["1"] = "15 kHz", ["2"] = "6 kHz",
            ["3"] = "3 kHz", ["4"] = "600 Hz", ["5"] = "300 Hz",
        };

    /// <summary>
    /// RF read answer (P3) -> display name. AUTO variants report the filter
    /// currently in circuit while the radio is in AUTO mode.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ReadCodeNames =
        new Dictionary<string, string>
        {
            ["1"] = "15 kHz", ["2"] = "6 kHz", ["3"] = "3 kHz",
            ["4"] = "Auto (15 kHz)", ["5"] = "Auto (6 kHz)", ["6"] = "Auto (3 kHz)",
            ["7"] = "600 Hz", ["8"] = "300 Hz",
            ["9"] = "Auto (600 Hz)", ["A"] = "Auto (300 Hz)",
        };

    // RF read answer (P3) -> dropdown set code (P2) so the UI stays in sync.
    // All AUTO variants collapse to the AUTO set code (0); the dropdown only
    // offers the six set codes.
    private static readonly IReadOnlyDictionary<string, string> ReadToSetCode =
        new Dictionary<string, string>
        {
            ["1"] = "1", ["2"] = "2", ["3"] = "3",
            ["7"] = "4", ["8"] = "5",
            ["4"] = "0", ["5"] = "0", ["6"] = "0", ["9"] = "0", ["A"] = "0",
        };

    /// <summary>
    /// Normalise an RF read-back code (P3) to the dropdown set code (P2).
    /// Unknown codes pass through unchanged so nothing is silently dropped.
    /// </summary>
    public static string NormalizeReadCode(string readCode) =>
        ReadToSetCode.GetValueOrDefault(readCode, readCode);
}
