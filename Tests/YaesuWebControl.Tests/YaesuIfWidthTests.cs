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
    }
}
