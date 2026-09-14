using System.Text;

namespace RadioWebControl.Core.Services.Cw
{
    /// <summary>
    /// Morse code lookup, in both directions, keyed on a dot/dash string where
    /// '.' is a dit and '-' is a dah (so "-.-." is C).
    ///
    /// Covers the ITU letters and digits, the punctuation that actually turns up
    /// on the air, and the common prosigns. Prosigns are run-together characters
    /// with no inter-character gap, so they arrive here as a single symbol and
    /// are rendered in angle brackets, which is what every other reader does and
    /// what an operator expects to see.
    ///
    /// Deliberately radio-agnostic and stateless: this is a table, the timing
    /// decisions live in CwElementDecoder.
    /// </summary>
    public static class MorseTable
    {
        private static readonly Dictionary<string, string> SymbolToText = new(StringComparer.Ordinal)
        {
            // Letters
            [".-"]     = "A", ["-..."]   = "B", ["-.-."]   = "C", ["-.."]    = "D",
            ["."]      = "E", ["..-."]   = "F", ["--."]    = "G", ["...."]   = "H",
            [".."]     = "I", [".---"]   = "J", ["-.-"]    = "K", [".-.."]   = "L",
            ["--"]     = "M", ["-."]     = "N", ["---"]    = "O", [".--."]   = "P",
            ["--.-"]   = "Q", [".-."]    = "R", ["..."]    = "S", ["-"]      = "T",
            ["..-"]    = "U", ["...-"]   = "V", [".--"]    = "W", ["-..-"]   = "X",
            ["-.--"]   = "Y", ["--.."]   = "Z",

            // Digits
            ["-----"]  = "0", [".----"]  = "1", ["..---"]  = "2", ["...--"]  = "3",
            ["....-"]  = "4", ["....."]  = "5", ["-...."]  = "6", ["--..."]  = "7",
            ["---.."]  = "8", ["----."]  = "9",

            // Punctuation seen on the air. Where a prosign shares a pattern with
            // punctuation the punctuation wins, because that is what it means in
            // an exchange: "-...-" is BT but reads as "=", and ".-.-." is AR but
            // reads as "+".
            [".-.-.-"]   = ".",  ["--..--"] = ",",  ["..--.."] = "?",  ["-....-"] = "-",
            ["-..-."]    = "/",  ["-.--."]  = "(",  ["-.--.-"] = ")",  [".----."] = "'",
            ["---..."]   = ":",  ["-.-.-."] = ";",  ["-...-"]  = "=",  [".-.-."]  = "+",
            [".--.-."]   = "@",  ["..--.-"] = "_",  ["...-..-"] = "$", ["-.-.--"] = "!",

            // Prosigns with no punctuation clash
            ["...-.-"]   = "<SK>",
            ["...-."]    = "<SN>",
            ["-.-.-"]    = "<CT>",
            ["........"] = "<HH>",
        };

        private static readonly Dictionary<char, string> TextToSymbol = BuildReverse();

        private static Dictionary<char, string> BuildReverse()
        {
            var map = new Dictionary<char, string>();
            foreach (var kv in SymbolToText)
            {
                if (kv.Value.Length != 1) continue;   // skip the bracketed prosigns
                map.TryAdd(kv.Value[0], kv.Key);
            }
            return map;
        }

        /// <summary>
        /// Decode one symbol. Returns null for anything not in the table, which
        /// is the normal outcome for a mis-timed or noise-corrupted character:
        /// the caller decides whether to emit a placeholder.
        /// </summary>
        public static string? Decode(string symbol)
            => string.IsNullOrEmpty(symbol) ? null
             : SymbolToText.TryGetValue(symbol, out var text) ? text
             : null;

        /// <summary>
        /// Encode a single character. Used only by the synthetic-audio test
        /// generator, but it belongs next to the table it mirrors.
        /// </summary>
        public static string? Encode(char c)
            => TextToSymbol.TryGetValue(char.ToUpperInvariant(c), out var sym) ? sym : null;

        /// <summary>
        /// Encode a whole string to dots, dashes and separators: a space between
        /// characters and a slash between words. Unknown characters are dropped.
        /// </summary>
        public static string EncodeText(string text)
        {
            var sb = new StringBuilder();
            bool pendingWordGap = false;

            foreach (char c in text)
            {
                if (c == ' ')
                {
                    pendingWordGap = sb.Length > 0;
                    continue;
                }

                var sym = Encode(c);
                if (sym is null) continue;

                if (sb.Length > 0) sb.Append(pendingWordGap ? '/' : ' ');
                pendingWordGap = false;
                sb.Append(sym);
            }

            return sb.ToString();
        }
    }
}
