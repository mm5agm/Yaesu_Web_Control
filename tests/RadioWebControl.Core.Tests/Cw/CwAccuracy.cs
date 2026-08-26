using System.Text;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// Scoring for the decoder tests. Levenshtein distance against the text that
    /// was actually sent, which counts a dropped character and a wrong one the
    /// same way - as it should, since both are equally unreadable on the screen.
    /// </summary>
    public static class CwAccuracy
    {
        /// <summary>Upper case, single spaces, no leading or trailing space.</summary>
        public static string Normalise(string s)
        {
            var sb = new StringBuilder();
            bool lastWasSpace = true;
            foreach (char c in s.ToUpperInvariant())
            {
                if (c == ' ')
                {
                    if (!lastWasSpace) sb.Append(' ');
                    lastWasSpace = true;
                }
                else
                {
                    sb.Append(c);
                    lastWasSpace = false;
                }
            }
            return sb.ToString().TrimEnd();
        }

        public static int EditDistance(string a, string b)
        {
            a = Normalise(a);
            b = Normalise(b);

            var prev = new int[b.Length + 1];
            var cur  = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                (prev, cur) = (cur, prev);
            }

            return prev[b.Length];
        }

        /// <summary>Fraction of the sent text that came through, 0..1.</summary>
        public static double Score(string decoded, string expected)
        {
            string exp = Normalise(expected);
            if (exp.Length == 0) return 1.0;
            int distance = EditDistance(decoded, exp);
            return Math.Max(0.0, 1.0 - (double)distance / exp.Length);
        }
    }
}
