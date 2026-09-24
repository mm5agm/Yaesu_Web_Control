namespace RadioWebControl.Core.Models
{
    /// <summary>
    /// One application memory: a frequency, a mode and, optionally, the
    /// receiver setup to restore with it. Shared by Icom Web Control and
    /// Yaesu Web Control. The optional fields hold the radio's own raw codes
    /// (an IF width code, an AGC code, an antenna number) exactly as the app
    /// read them, so nothing in here knows or cares which radio wrote them --
    /// the app that recalls the memory is the one that knows what "8" means.
    /// </summary>
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
        public string? Antenna { get; set; }        // antenna number as the radio reports it, e.g. "1" / "2" / "3"
        public string? IfWidthCode { get; set; }    // the radio's raw IF width code, e.g. "8"
        public int? IfShiftHz { get; set; }         // IF shift in Hz; the radio's own step applies
        public string? RoofingCode { get; set; }    // the radio's raw roofing filter code
        public bool? NbOn { get; set; }
        public int? NbLevel { get; set; }           // noise blanker level in the radio's own range
        public string? NrLevel { get; set; }        // noise reduction as the radio codes it: "0" off, then its own levels
        public string? AgcMode { get; set; }        // the radio's raw AGC code
        public int? PowerWatts { get; set; }        // RF power in watts (the app clamps to the model's maximum)
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
