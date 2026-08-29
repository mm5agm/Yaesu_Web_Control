namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// SH filter-width code to bandwidth in Hz, per radio and per mode.
    ///
    /// The browser has had this table since the Filter Function Display was
    /// built (wwwroot/js/ui/if-width-tables.js). The CW decoder needs it too,
    /// and the decoder runs server-side, so the table has to exist here as
    /// well. YaesuIfWidthTests parses the JavaScript and asserts the two agree,
    /// which is what stops them drifting apart.
    ///
    /// The SH command takes one code per radio but means different bandwidths
    /// in different modes: code 8 is 1650 Hz in SSB and 400 Hz in CW. Anything
    /// converting a code has to know the mode, which is the whole reason this
    /// is a lookup and not arithmetic. (Icom is the other way round - the
    /// IC-7300 MkII's 1A 03 gives a code that maps to Hz by formula, so Icom
    /// Web Control stores Hz directly and needs none of this.)
    ///
    /// Data is from Table 3 of each radio's CAT manual, except where noted.
    /// </summary>
    public static class YaesuIfWidth
    {
        /// <summary>
        /// Which bandwidth column a mode reads from. Null means the radio does
        /// not offer an IF width in that mode at all - AM and FM - and callers
        /// must treat that as "no width", not as zero.
        /// </summary>
        public static string? ModeGroup(string? mode) => mode switch
        {
            null                             => "ssb",
            "LSB" or "USB"                   => "ssb",
            "DATA-L" or "DATA-U"             => "ssb",
            "CW-U" or "CW-L"                 => "cw",
            "RTTY-L" or "RTTY-U"             => "cw",
            "PSK"                            => "cw",
            "AM" or "AM-N"                   => null,
            "FM" or "FM-N"                   => null,
            "DATA-FM" or "DATA-FM-N"         => null,
            _                                => "ssb",
        };

        // Code 0 is the radio's mode-dependent default and resolves to no known
        // width, so it is absent from every table here rather than present as a
        // sentinel. The JavaScript stores it as the string 'default' and the
        // drift test treats the two spellings as equivalent.

        private static readonly Dictionary<int, int> Dx101Ssb = new()
        {
            // Codes 1-21 per the 2308-L CAT manual; 22 and 23 verified on
            // Colin's FTdx101MP (issue #50) - current firmware extends the SSB
            // set to 3.5 and 4 kHz.
            [1] = 300, [2] = 400, [3] = 600, [4] = 850, [5] = 1100, [6] = 1200,
            [7] = 1500, [8] = 1650, [9] = 1800, [10] = 1950, [11] = 2100,
            [12] = 2200, [13] = 2300, [14] = 2400, [15] = 2500, [16] = 2600,
            [17] = 2700, [18] = 2800, [19] = 2900, [20] = 3000, [21] = 3200,
            [22] = 3500, [23] = 4000,
        };

        private static readonly Dictionary<int, int> Dx101Cw = new()
        {
            // Codes 1-18 per the manual; 19/20/21 verified on the MP (issue
            // #50). Same kHz values as SSB codes 21/22/23 but at different code
            // numbers, which is the mode-awareness this class exists for.
            [1] = 50, [2] = 100, [3] = 150, [4] = 200, [5] = 250, [6] = 300,
            [7] = 350, [8] = 400, [9] = 450, [10] = 500, [11] = 600, [12] = 800,
            [13] = 1200, [14] = 1400, [15] = 1700, [16] = 2000, [17] = 2400,
            [18] = 3000, [19] = 3200, [20] = 3500, [21] = 4000,
        };

        private static readonly Dictionary<int, int> Dx10Ssb = new()
        {
            [1] = 300, [2] = 400, [3] = 600, [4] = 850, [5] = 1100, [6] = 1200,
            [7] = 1500, [8] = 1650, [9] = 1800, [10] = 1950, [11] = 2100,
            [12] = 2250, [13] = 2400, [14] = 2450, [15] = 2500, [16] = 2600,
            [17] = 2700, [18] = 2800, [19] = 2900, [20] = 3000, [21] = 3200,
            [22] = 3500, [23] = 4000,
        };

        private static readonly Dictionary<int, int> Dx10Cw = new()
        {
            [1] = 50, [2] = 100, [3] = 150, [4] = 200, [5] = 250, [6] = 300,
            [7] = 350, [8] = 400, [9] = 450, [10] = 500, [11] = 600, [12] = 800,
            [13] = 1200, [14] = 1400, [15] = 1700, [16] = 2000, [17] = 2400,
            [18] = 3000, [19] = 3200, [20] = 3500, [21] = 4000,
        };

        // The FT-710 exposes only specific codes, not the full 0-23 range. The
        // gaps are the radio's, not an omission here.
        private static readonly Dictionary<int, int> Ft710Ssb = new()
        {
            [1] = 300, [3] = 850, [5] = 1100, [7] = 1500, [9] = 1800,
            [12] = 2250, [16] = 2600, [19] = 2900, [20] = 3200, [21] = 3500,
            [22] = 4000,
        };

        private static readonly Dictionary<int, int> Ft710Cw = new()
        {
            [1] = 50, [3] = 150, [5] = 250, [7] = 350, [9] = 450,
            [12] = 800, [16] = 2000, [19] = 3200, [20] = 3500, [21] = 4000,
        };

        // FTDX3000 Wide bandwidths only - Narrow has fewer steps. Codes are
        // non-contiguous.
        private static readonly Dictionary<int, int> Dx3000Ssb = new()
        {
            [1] = 200, [2] = 400, [3] = 600, [4] = 850, [6] = 1350, [7] = 1500,
            [9] = 1800, [12] = 2200, [14] = 2400, [16] = 2600, [18] = 2800,
            [20] = 3000, [22] = 3400, [25] = 4000,
        };

        private static readonly Dictionary<int, int> Dx3000Cw = new()
        {
            [10] = 500, [11] = 800, [12] = 1200, [13] = 1400, [14] = 1700,
            [15] = 2000, [16] = 2400,
        };

        private static readonly Dictionary<string, (Dictionary<int, int> Ssb, Dictionary<int, int> Cw)> Tables =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["FTdx101MP"] = (Dx101Ssb, Dx101Cw),
                ["FTdx101D"]  = (Dx101Ssb, Dx101Cw),   // shares the MP tables
                ["FTdx10"]    = (Dx10Ssb,  Dx10Cw),
                ["FT-710"]    = (Ft710Ssb, Ft710Cw),
                ["FTDX3000"]  = (Dx3000Ssb, Dx3000Cw),
            };

        /// <summary>Models this class knows, for tests and diagnostics.</summary>
        public static IReadOnlyCollection<string> KnownModels => Tables.Keys;

        /// <summary>
        /// Bandwidth in Hz for a model, mode and SH code, or null when there is
        /// no answer: an unknown model, a mode with no IF width, code 0 (the
        /// radio's own default, whose width it does not report), or a code the
        /// radio does not expose.
        ///
        /// Null means "do not know", and callers must not substitute a guess -
        /// the decoder's search window falls back to its default instead, which
        /// is a deliberate choice rather than a wrong number.
        /// </summary>
        public static int? HzFor(string? model, string? mode, int code)
        {
            string? group = ModeGroup(mode);
            if (group is null) return null;
            if (model is null || !Tables.TryGetValue(model, out var t)) return null;

            var table = group == "cw" ? t.Cw : t.Ssb;
            return table.TryGetValue(code, out int hz) ? hz : null;
        }

        /// <summary>
        /// The same lookup from the raw state string the CAT layer stores,
        /// which is an SH code such as "8" and may be empty or unparseable.
        /// </summary>
        public static int? HzForCode(string? model, string? mode, string? code)
            => int.TryParse(code, out int c) ? HzFor(model, mode, c) : null;
    }
}
