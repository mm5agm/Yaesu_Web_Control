using System;
using System.Collections.Generic;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Where each radio keeps its RTTY tone settings in the extended (EX) menu,
    /// and how to turn the answer codes into Hz.
    ///
    /// This exists because click-to-tune in RTTY has to know the audio frequency
    /// the operator's decoder is listening for, exactly as the CW case has to
    /// know the sidetone pitch. The pitch comes from the radio (the KP setting);
    /// so should these, rather than assuming everyone runs the defaults.
    ///
    /// Addresses are transcribed from each radio's own CAT manual in
    /// docs/manuals/ - the RTTY group is P1=01 P2=05 on every hierarchical
    /// radio, but the item numbers within it differ per model, so they cannot
    /// be shared:
    ///
    ///   FTdx101MP/D  EX 01 05 04 POLARITY-RX,  13 MARK FREQUENCY, 14 SHIFT FREQUENCY
    ///   FTdx10       EX 01 05 07 POLARITY-RX,  15 MARK FREQUENCY, 16 SHIFT FREQUENCY
    ///   FT-710       EX 01 05    (no RX pol),  15 MARK FREQUENCY, 16 SHIFT FREQUENCY
    ///   FTDX3000     EX 094 POLARITY-RX, 097 SHIFT, 098 MARK   (flat addressing)
    ///
    /// The FT-710 CAT manual lists only POLARITY-TX in this group, so RX
    /// polarity is left at its default there. A radio that isn't in the table
    /// at all falls back to the Yaesu defaults, which is what the overwhelming
    /// majority of operators run anyway.
    ///
    /// Yaesu-specific register addressing, so this stays out of core/.
    /// </summary>
    public static class RttyToneMap
    {
        /// <summary>EX addresses for one radio. Null members are not exposed by that model.</summary>
        public sealed record Addresses(string? PolarityRx, string? MarkFreq, string? ShiftFreq);

        // Hierarchical radios use the full P1P2P3 string; flat radios use the
        // three-digit menu ID. Either way it is the text that follows "EX".
        private static readonly Dictionary<string, Addresses> Map = new(StringComparer.OrdinalIgnoreCase)
        {
            ["FTdx101MP"] = new("010504", "010513", "010514"),
            ["FTdx101D"]  = new("010504", "010513", "010514"),
            ["FTdx10"]    = new("010507", "010515", "010516"),
            ["FT-710"]    = new(null,     "010515", "010516"),
            ["FTDX3000"]  = new("094",    "098",    "097"),
        };

        /// <summary>Yaesu's own defaults: 2125 Hz mark, 170 Hz shift, normal polarity.</summary>
        public const int DefaultMarkHz  = 2125;
        public const int DefaultShiftHz = 170;

        public static Addresses? For(string? radioModel)
            => radioModel != null && Map.TryGetValue(radioModel, out var a) ? a : null;

        /// <summary>MARK FREQUENCY answer code to Hz. 1 = 1275, 2 = 2125.</summary>
        public static int? MarkHzFromCode(string? code) => code?.TrimStart('0') switch
        {
            "1" => 1275,
            "2" => 2125,
            _   => null,
        };

        /// <summary>
        /// SHIFT FREQUENCY answer code to Hz. The CAT manuals print this row as
        /// "1: 170 Hz 1: 200 Hz 2: 425 Hz 3: 850 Hz" - the first code is a
        /// typo for 0, since four options cannot share three codes and every
        /// other four-way list in the same table runs 0-3. It is read here in
        /// the printed order (0,1,2,3). Getting that wrong costs at most
        /// 15 Hz of tuning offset, because the shift only ever contributes
        /// half of itself to the click offset.
        /// </summary>
        public static int? ShiftHzFromCode(string? code) => code?.TrimStart('0') switch
        {
            ""  => 170,   // "0" trims to empty
            "1" => 200,
            "2" => 425,
            "3" => 850,
            _   => null,
        };
    }
}
