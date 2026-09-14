using System.Text.RegularExpressions;
using Xunit;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// The SH code to Hz tables exist twice: in the browser for the Filter
    /// Function Display, and in C# for the CW decoder, which runs server-side
    /// and cannot reach the browser's copy. Two transcriptions of the same CAT
    /// manual drift apart, so these tests parse the JavaScript and hold the C#
    /// to it.
    ///
    /// If this fails after an edit to one copy, the fix is to make the other
    /// match. Relaxing the test just restores the drift it exists to prevent.
    /// </summary>
    public class YaesuIfWidthTests
    {
        [Theory]
        [InlineData("FTdx101MP", "USB", 8, 1650)]
        [InlineData("FTdx101MP", "CW-U", 8, 400)]   // same code, different mode
        [InlineData("FTdx101MP", "CW-U", 10, 500)]
        [InlineData("FTdx10", "USB", 12, 2250)]     // differs from the 101's 2200
        [InlineData("FT-710", "CW-U", 12, 800)]
        [InlineData("FTDX3000", "CW-U", 10, 500)]
        public void KnownWidths(string model, string mode, int code, int expected)
            => Assert.Equal(expected, YaesuIfWidth.HzFor(model, mode, code));

        [Theory]
        [InlineData("FTdx101MP", "AM", 8)]      // AM offers no IF width at all
        [InlineData("FTdx101MP", "FM", 8)]
        [InlineData("FTdx101MP", "CW-U", 0)]    // 0 is the radio's own default
        [InlineData("FTdx101MP", "CW-U", 99)]   // not a code the radio exposes
        [InlineData("FT-710", "CW-U", 2)]       // the FT-710's codes are sparse
        [InlineData("NoSuchRig", "CW-U", 8)]
        public void NoAnswerIsNull(string model, string mode, int code)
            => Assert.Null(YaesuIfWidth.HzFor(model, mode, code));

        [Fact]
        public void ModelSetMatchesJavaScript()
        {
            var js = ParseJavaScriptTables();
            Assert.Equal(
                js.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray(),
                YaesuIfWidth.KnownModels.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray());
        }

        [Fact]
        public void EveryCodeMatchesJavaScript()
        {
            var js = ParseJavaScriptTables();
            var mismatches = new List<string>();

            foreach (var (model, groups) in js)
            {
                foreach (var (group, table) in groups)
                {
                    string mode = group == "cw" ? "CW-U" : "USB";

                    // Exhaustive over the code space either copy could use, so a
                    // code present in one and absent from the other fails
                    // whichever side it is missing from.
                    for (int code = 0; code <= 40; code++)
                    {
                        int? fromJs = table.TryGetValue(code, out int hz) ? hz : (int?)null;
                        int? fromCs = YaesuIfWidth.HzFor(model, mode, code);
                        if (fromJs != fromCs)
                            mismatches.Add($"{model}/{group} code {code}: js={Show(fromJs)} cs={Show(fromCs)}");
                    }
                }
            }

            Assert.True(mismatches.Count == 0,
                "C# and JavaScript IF width tables disagree:" + Environment.NewLine + "  " +
                string.Join(Environment.NewLine + "  ", mismatches));

            static string Show(int? v) => v?.ToString() ?? "none";
        }

        // ---- the JavaScript side -------------------------------------------

        /// <summary>model -> group ("ssb"/"cw") -> code -> Hz, read from the JS.</summary>
        private static Dictionary<string, Dictionary<string, Dictionary<int, int>>> ParseJavaScriptTables()
        {
            string text = File.ReadAllText(LocateJavaScript());

            // Strip line comments first: the tables are heavily annotated and
            // the comments contain digits and colons that would otherwise parse
            // as entries.
            text = Regex.Replace(text, @"//[^\n]*", "");

            string tables = BalancedBlock(text, text.IndexOf("const TABLES", StringComparison.Ordinal));
            var result = new Dictionary<string, Dictionary<string, Dictionary<int, int>>>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in Regex.Matches(tables, @"'(?<model>[^']+)'\s*:\s*\{"))
            {
                string body = BalancedBlock(tables, m.Index + m.Length - 1);
                var groups = new Dictionary<string, Dictionary<int, int>>();

                foreach (Match g in Regex.Matches(body, @"\b(?<group>ssb|cw)\s*:\s*\{"))
                {
                    string groupBody = BalancedBlock(body, g.Index + g.Length - 1);
                    var codes = new Dictionary<int, int>();
                    foreach (Match e in Regex.Matches(groupBody, @"(?<code>\d+)\s*:\s*(?<hz>\d+|'default')"))
                    {
                        // 'default' is code 0's placeholder: the radio decides
                        // and does not say what it decided, so it is the same as
                        // having no entry at all.
                        if (e.Groups["hz"].Value == "'default'") continue;
                        codes[int.Parse(e.Groups["code"].Value)] = int.Parse(e.Groups["hz"].Value);
                    }
                    groups[g.Groups["group"].Value] = codes;
                }

                if (groups.Count > 0) result[m.Groups["model"].Value] = groups;
            }

            // Aliases assigned after the literal, e.g.
            //   TABLES['FTdx101D'] = TABLES['FTdx101MP'];
            foreach (Match a in Regex.Matches(text,
                         @"TABLES\['(?<alias>[^']+)'\]\s*=\s*TABLES\['(?<target>[^']+)'\]"))
            {
                if (result.TryGetValue(a.Groups["target"].Value, out var target))
                    result[a.Groups["alias"].Value] = target;
            }

            Assert.NotEmpty(result);
            return result;
        }

        /// <summary>The { ... } block at or after <paramref name="from"/>, brace-matched.</summary>
        private static string BalancedBlock(string text, int from)
        {
            int open = text.IndexOf('{', from);
            Assert.True(open >= 0, "no opening brace found");

            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0)
                    return text.Substring(open + 1, i - open - 1);
            }

            throw new InvalidOperationException("unbalanced braces in if-width-tables.js");
        }

        /// <summary>
        /// Walk up from the test assembly to the repository root. The binary
        /// sits several levels below it and the depth differs between a local
        /// build and a published one, so search rather than assume.
        /// </summary>
        private static string LocateJavaScript()
        {
            const string relative = "wwwroot/js/ui/if-width-tables.js";
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            throw new FileNotFoundException(
                $"Could not find {relative} above {AppContext.BaseDirectory}. This test " +
                "compares the C# IF width tables against the browser's copy and needs both.");
        }
    
        // ---- CodeForHz: the reverse lookup Reader Mode needs ----------------

        [Theory]
        [InlineData("FTdx101MP", 250, 5)]    // exactly on a step
        [InlineData("FTdx101MP", 260, 5)]    // nearer 250 than 300
        [InlineData("FTdx101MP", 280, 6)]    // nearer 300 than 250
        [InlineData("FTdx10",    250, 5)]
        [InlineData("FT-710",    250, 5)]    // the 710 has gaps; 250 is one it has
        [InlineData("FT-710",    200, 5)]    // 200 is not on the 710 at all: 150 and 250 tie, wider wins
        [InlineData("FTDX3000",  250, 10)]   // its CW set starts at 500
        public void The_nearest_available_CW_width_is_chosen(string model, int wantedHz, int expectedCode)
        {
            Assert.Equal(expectedCode, YaesuIfWidth.CodeForHz(model, "CW-U", wantedHz));
        }

        [Fact]
        public void A_tie_resolves_to_the_wider_filter()
        {
            // 175 Hz is exactly between the 101's 150 (code 3) and 200 (code 4).
            // Wider wins, because a filter narrower than asked for can put the
            // CW pitch outside the passband and lose a signal the operator can
            // hear - which is the opposite of what Reader Mode is for.
            Assert.Equal(4, YaesuIfWidth.CodeForHz("FTdx101MP", "CW-U", 175));
        }

        [Fact]
        public void A_width_below_everything_the_radio_has_still_gets_the_narrowest()
        {
            // Asking a FTDX3000 for 50 Hz is asking for something it does not
            // have. Refusing would leave the filter wide open, which helps
            // nobody; 500 Hz is the best it can do and is still a large
            // improvement on 2.4 kHz.
            Assert.Equal(10, YaesuIfWidth.CodeForHz("FTDX3000", "CW-U", 50));
        }

        [Fact]
        public void The_answer_is_mode_aware_because_the_codes_are()
        {
            // Code 5 is 250 Hz in CW and 1100 Hz in SSB on the 101. A reverse
            // lookup that ignored the mode would hand Reader Mode a code that
            // means something else entirely.
            Assert.Equal(5,  YaesuIfWidth.CodeForHz("FTdx101MP", "CW-U", 250));
            Assert.Equal(1,  YaesuIfWidth.CodeForHz("FTdx101MP", "USB",  250));
        }

        [Theory]
        [InlineData("FT-991A", "CW-U")]   // a model with no table here
        [InlineData("FTdx101MP", "AM")]   // a mode with no IF width at all
        [InlineData("FTdx101MP", "FM")]
        public void No_table_means_no_answer_rather_than_a_guess(string model, string mode)
        {
            // Null is the instruction to leave the operator's filter alone. A
            // guessed SH code would set a bandwidth nobody chose on a rig
            // nobody here has tested against.
            Assert.Null(YaesuIfWidth.CodeForHz(model, mode, 250));
        }

        [Fact]
        public void Every_code_it_returns_maps_back_to_a_real_width()
        {
            // The round trip is the actual contract: whatever CodeForHz picks
            // has to be a code HzFor recognises, or Reader Mode would send the
            // radio an SH code the rest of the app cannot interpret.
            foreach (var model in YaesuIfWidth.KnownModels)
            {
                for (int wanted = 50; wanted <= 4000; wanted += 25)
                {
                    var code = YaesuIfWidth.CodeForHz(model, "CW-U", wanted);
                    Assert.NotNull(code);
                    Assert.NotNull(YaesuIfWidth.HzFor(model, "CW-U", code!.Value));
                }
            }
        }
}
}
