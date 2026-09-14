namespace RadioWebControl.Core.Models
{
    /// <summary>
    /// A single DX spot received from a cluster server.
    /// </summary>
    public class DxSpot
    {
        /// <summary>Callsign being spotted.</summary>
        public string Callsign { get; set; } = "";

        /// <summary>Frequency in Hz (cluster gives kHz to one decimal; we normalise to Hz).</summary>
        public long FrequencyHz { get; set; }

        /// <summary>The station that reported the spot.</summary>
        public string Spotter { get; set; } = "";

        /// <summary>Free-text comment field — often contains mode hints (FT8, CW, SSB) or notes.</summary>
        public string Comment { get; set; } = "";

        /// <summary>Time the spot was received by this app (UTC).</summary>
        public DateTime ReceivedUtc { get; set; }

        /// <summary>True if this spot matches one of the user's watched callsigns/prefixes.</summary>
        public bool IsWatched { get; set; }
    }
}
