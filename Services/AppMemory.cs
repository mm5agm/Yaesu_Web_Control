namespace Yaesu_Web_Control.Services
{
    public class AppMemory
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
        public long FrequencyHz { get; set; }
        public string Mode { get; set; } = "USB";
        public int ClarifierOffsetHz { get; set; }
        public bool RxClarOn { get; set; }
        public bool TxClarOn { get; set; }
        public int SortOrder { get; set; }

        // Optional / "advanced" fields. Null = don't apply this on recall, leave the
        // radio's current value alone. Existing memories.json files without these
        // properties deserialize with nulls automatically — backwards compatible.
        public string? Antenna { get; set; }        // "1" / "2" / "3"
        public string? IfWidthCode { get; set; }    // raw SH code, e.g. "8"
        public int? IfShiftHz { get; set; }         // -1000..+1000 in 20 Hz steps
        public string? RoofingCode { get; set; }    // "6","7","8","9","A" on FTdx101; "0"-"5" on FTDX3000
        public bool? NbOn { get; set; }
        public int? NbLevel { get; set; }           // 1..20
        public string? NrLevel { get; set; }        // "0" off, "1" on (DNR on/off; the name predates #144)
        public string? AgcMode { get; set; }        // "0" off, "1" fast, "2" mid, "3" slow, "4" auto
        public int? PowerWatts { get; set; }        // 5..200 (radio dependent max)
        public string? Notes { get; set; }

        /// <summary>
        /// A field-for-field copy. Memory banks hold copies, never the live
        /// objects, and the copy must carry every field: until 2026-09-20
        /// the bank loader copied only the six basic ones, so the receiver
        /// setup saved with "Save to Mem" was silently lost on every bank
        /// load. A new property added here is copied by MemberwiseClone
        /// without being listed, which is the point of using it.
        /// </summary>
        public AppMemory Clone() => (AppMemory)MemberwiseClone();
    }
}
