using System.Globalization;

namespace RadioWebControl.Core.Services
{
    /// <summary>
    /// Minimal ADIF (Amateur Data Interchange Format) parser. Reads only the
    /// fields the memory-import feature cares about — FREQ, MODE, BAND,
    /// CALL — and ignores everything else. Tolerates header (skipped via
    /// &lt;EOH&gt;), arbitrary whitespace, mixed-case field names, and DOS
    /// or Unix line endings.
    ///
    /// Format reference: ADIF 3.x. Each field is "&lt;FIELD:LENGTH&gt;value"
    /// (optionally with a type after the length, "&lt;FIELD:LENGTH:T&gt;value"
    /// which we ignore). Each record terminates with "&lt;EOR&gt;".
    /// </summary>
    public static class AdifParser
    {
        public class AdifRecord
        {
            public string? Frequency  { get; set; } // raw value, MHz string (e.g. "14.074")
            public string? Mode       { get; set; }
            public string? Band       { get; set; }
            public string? Callsign   { get; set; }
        }

        public static List<AdifRecord> Parse(string content)
        {
            var records = new List<AdifRecord>();
            if (string.IsNullOrEmpty(content)) return records;

            // Skip header if present. ADIF header ends with <EOH>.
            int start = 0;
            int eohIdx = IndexOfTag(content, "EOH", 0);
            if (eohIdx >= 0) start = eohIdx;

            var current = new AdifRecord();
            int i = start;
            while (i < content.Length)
            {
                int open = content.IndexOf('<', i);
                if (open < 0) break;
                int close = content.IndexOf('>', open + 1);
                if (close < 0) break;

                var tag = content.Substring(open + 1, close - open - 1);
                // tag is e.g. "CALL:6" or "CALL:6:S" or "EOR" or "EOH"
                var parts = tag.Split(':');
                var name = parts[0].Trim().ToUpperInvariant();

                if (name == "EOR")
                {
                    if (current.Frequency != null || current.Mode != null || current.Band != null || current.Callsign != null)
                        records.Add(current);
                    current = new AdifRecord();
                    i = close + 1;
                    continue;
                }
                if (name == "EOH")
                {
                    i = close + 1;
                    continue;
                }

                if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int len) || len < 0)
                {
                    // Malformed tag — skip to next '<'
                    i = close + 1;
                    continue;
                }

                int valueStart = close + 1;
                if (valueStart + len > content.Length) break;
                var value = content.Substring(valueStart, len).Trim();
                i = valueStart + len;

                switch (name)
                {
                    case "FREQ":  current.Frequency = value; break;
                    case "MODE":  current.Mode      = value; break;
                    case "BAND":  current.Band      = value; break;
                    case "CALL":  current.Callsign  = value; break;
                }
            }
            // Catch a trailing record without a final <EOR> (uncommon but seen)
            if (current.Frequency != null || current.Mode != null || current.Band != null || current.Callsign != null)
                records.Add(current);

            return records;
        }

        /// <summary>Parse the FREQ field (ADIF stores MHz as a decimal string) into Hz.</summary>
        public static long? FreqMHzToHz(string? freq)
        {
            if (string.IsNullOrWhiteSpace(freq)) return null;
            if (!decimal.TryParse(freq, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal mhz)) return null;
            if (mhz <= 0) return null;
            return (long)Math.Round(mhz * 1_000_000m);
        }

        /// <summary>
        /// Map an ADIF MODE string to one of the app's radio mode names. ADIF
        /// uses a flat list of modes; the app's "modes" include the upper/lower
        /// distinction for CW, RTTY and DATA which ADIF doesn't carry. We
        /// pick a sensible default for ambiguous cases.
        /// </summary>
        public static string AdifModeToRadioMode(string? mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return "USB";
            switch (mode.Trim().ToUpperInvariant())
            {
                case "USB":  return "USB";
                case "LSB":  return "LSB";
                case "AM":   return "AM";
                case "FM":   return "FM";
                case "CW":   return "CW-U";
                case "RTTY": return "RTTY-L"; // traditional RTTY is LSB-keyed
                case "FT8":
                case "FT4":
                case "JT65":
                case "JT9":
                case "MFSK":
                case "JS8":
                case "PKT":
                case "PSK":
                case "PSK31":
                case "PSK63":
                case "DIGITALVOICE":
                case "DATA":   return "DATA-U";
                default:        return "USB";
            }
        }

        private static int IndexOfTag(string content, string tagName, int start)
        {
            int i = start;
            while (i < content.Length)
            {
                int open = content.IndexOf('<', i);
                if (open < 0) return -1;
                int close = content.IndexOf('>', open + 1);
                if (close < 0) return -1;
                var inner = content.Substring(open + 1, close - open - 1);
                var name = inner.Split(':')[0].Trim().ToUpperInvariant();
                if (name == tagName.ToUpperInvariant()) return close + 1;
                i = close + 1;
            }
            return -1;
        }
    }
}
