using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RadioWebControl.Core.Services.Cw
{
    /// <summary>
    /// Picks the fields of a QSO out of decoded CW, so the log form can be
    /// offered pre-filled instead of empty.
    ///
    /// Everything here is a <em>suggestion</em>, and the shape of the API says
    /// so: candidates come back ranked, with the evidence that ranked them, and
    /// nothing is ever asserted as the answer. That is not politeness. Section
    /// 4.11h measured the decoder's own confidence at 1.00 on 592 characters of
    /// junk, so text arriving here may be rubbish that reads like a QSO, and a
    /// log entry silently filled from it is worse than an empty form - the
    /// operator has no reason to look twice at a field already filled in.
    ///
    /// It is pure: text in, candidates out. No radio, no clock, no files.
    /// </summary>
    public static class CwQsoFields
    {
        /// <summary>A suggested value, with why it was suggested and how strongly.</summary>
        public readonly record struct Candidate(string Value, double Score, string Why);

        // A callsign is one or two letters, or a letter-digit pair, then a
        // digit, then one to four letters. That covers 2E0AAA, MM5AGM, W1AW,
        // 9A1A and VP2E-style prefixes. A portable or operating suffix is
        // captured as a whole so /P and /QRP need not be enumerated.
        private static readonly Regex CallPattern = new(
            @"^(?:[A-Z]{1,2}[0-9]|[0-9][A-Z]{1,2}|[A-Z][0-9][A-Z])[0-9]?[A-Z]{1,4}(?:/[A-Z0-9]{1,4})?$",
            RegexOptions.Compiled);

        // The words a callsign follows in almost every CW exchange. DE is the
        // strongest by a distance: it means "from", so what follows it is the
        // other operator naming themselves.
        private static readonly string[] AfterDe = { "DE" };
        private static readonly string[] AfterCq = { "CQ", "TEST", "QRZ" };

        // Words shaped exactly like callsigns that are not callsigns. Every one
        // of these has been seen in ordinary CW traffic.
        private static readonly HashSet<string> NotCalls = new(StringComparer.Ordinal)
        {
            "R5NN", "T5NN", "N5NN", "5NN", "599",
            "QRZ", "QRM", "QRN", "QRP", "QSB", "QSL", "QSO", "QTH", "QRS", "QRO",
            "73", "88", "CQ", "TU", "TNX", "TKS", "RST", "UR",
        };

        /// <summary>
        /// Callsigns the text might contain, best first.
        ///
        /// The ranking is entirely positional, because that is the only
        /// evidence available without a callsign database: a word after DE is
        /// almost certainly a call, a word after CQ is the caller's own, and a
        /// bare call-shaped word in the middle of a sentence might be anything.
        /// Repetition counts too - operators send their callsign two and three
        /// times precisely because it is the part that has to survive, and a
        /// word that arrived twice in the same shape is far less likely to be
        /// two identical decoding errors than one lucky one.
        /// </summary>
        public static IReadOnlyList<Candidate> Callsigns(string? decoded)
        {
            var words = Words(decoded);
            if (words.Count == 0) return Array.Empty<Candidate>();

            var scores = new Dictionary<string, double>(StringComparer.Ordinal);
            var why    = new Dictionary<string, string>(StringComparer.Ordinal);

            void Add(string call, double weight, string reason)
            {
                if (!scores.TryGetValue(call, out double had) || weight > had) why[call] = reason;
                scores[call] = had + weight;
            }

            for (int i = 0; i < words.Count; i++)
            {
                string w = words[i];
                if (!LooksLikeCall(w)) continue;

                string prev = i > 0 ? words[i - 1] : string.Empty;
                if (AfterDe.Contains(prev))      Add(w, 3.0, "follows DE");
                else if (AfterCq.Contains(prev)) Add(w, 2.0, "follows CQ");
                else                             Add(w, 1.0, "call-shaped");
            }

            // Repetition, counted once per extra appearance rather than per
            // appearance, so a station sending its call three times does not
            // out-score a call that arrived once directly after DE by sheer
            // volume when the repeats are the same evidence seen again.
            foreach (var g in words.Where(LooksLikeCall).GroupBy(w => w, StringComparer.Ordinal))
            {
                if (g.Count() <= 1 || !scores.ContainsKey(g.Key)) continue;
                scores[g.Key] += 0.5 * (g.Count() - 1);
                if (why[g.Key] == "call-shaped") why[g.Key] = "sent " + g.Count() + " times";
            }

            return scores
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new Candidate(kv.Key, kv.Value, why[kv.Key]))
                .ToList();
        }

        /// <summary>
        /// The signal report the other operator sent, expanded from cut numbers.
        ///
        /// Contest and ragchew CW both abbreviate digits to letters that are
        /// quicker to send - 5NN is 599, 5TT is 500 - and the decoder
        /// transcribes exactly what was keyed, which is right: it is not the
        /// decoder's place to guess. It is this layer's place, because a log
        /// wants the number.
        ///
        /// Only the three cut numbers that appear in reports are expanded, and
        /// only inside a word that is otherwise all digits and cut letters.
        /// Expanding T to 0 everywhere would turn every bare T in ordinary text
        /// into a digit.
        /// </summary>
        public static IReadOnlyList<Candidate> SignalReports(string? decoded)
        {
            var words = Words(decoded);
            var found = new List<Candidate>();

            for (int i = 0; i < words.Count; i++)
            {
                string w = words[i];
                string expanded = ExpandCutNumbers(w);
                if (expanded.Length is < 2 or > 3) continue;
                if (!expanded.All(char.IsDigit)) continue;

                // Readability 1-5, strength 1-9, tone 1-9. A "report" outside
                // that is a serial number, an age or a temperature.
                if (expanded[0] < '1' || expanded[0] > '5') continue;
                if (expanded[1] < '1' || expanded[1] > '9') continue;
                if (expanded.Length == 3 && (expanded[2] < '1' || expanded[2] > '9')) continue;

                string prev = i > 0 ? words[i - 1] : string.Empty;
                bool cued = prev == "RST" || prev == "UR" || prev == "URS";
                double score = cued ? 3.0 : 1.0;
                if (expanded != w) score += 0.5;   // cut numbers are CW, not stray digits

                string reason = expanded != w
                    ? (cued ? "follows " + prev + ", cut numbers" : "cut numbers")
                    : (cued ? "follows " + prev                   : "report-shaped");
                found.Add(new Candidate(expanded, score, reason));
            }

            return Merge(found);
        }

        /// <summary>
        /// The operator's name, which in CW follows NAME or OP and almost
        /// nothing else. Worth picking out because it is the field an operator
        /// most wants in the log and least wants to retype.
        /// </summary>
        public static IReadOnlyList<Candidate> Names(string? decoded) => After(decoded, "NAME", "OP");

        /// <summary>The station location, which follows QTH.</summary>
        public static IReadOnlyList<Candidate> Locations(string? decoded) => After(decoded, "QTH");

        private static IReadOnlyList<Candidate> After(string? decoded, params string[] keywords)
        {
            var words = Words(decoded);
            var found = new List<Candidate>();

            for (int i = 0; i < words.Count - 1; i++)
            {
                if (!keywords.Contains(words[i])) continue;

                // "NAME IS BOB" is as common as "NAME BOB".
                string next = words[i + 1] == "IS" && i + 2 < words.Count ? words[i + 2] : words[i + 1];
                if (next.Length < 2 || !next.All(char.IsLetter)) continue;
                if (LooksLikeCall(next)) continue;
                found.Add(new Candidate(next, 1.0, "follows " + words[i]));
            }

            return Merge(found);
        }

        private static IReadOnlyList<Candidate> Merge(List<Candidate> found) => found
            .GroupBy(c => c.Value, StringComparer.Ordinal)
            .Select(g => new Candidate(g.Key, g.Sum(c => c.Score),
                                       g.OrderByDescending(c => c.Score).First().Why))
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Value, StringComparer.Ordinal)
            .ToList();

        /// <summary>
        /// T for zero, N for nine, A for one - the three cut numbers used in
        /// signal reports. Applied only to a word that is all digits and cut
        /// letters <em>and contains at least one real digit</em>, so ordinary
        /// text is never touched.
        ///
        /// That digit is not a nicety. ANT, TNT and NNN are all made entirely
        /// of cut letters and all appear in ordinary CW - ANT especially, since
        /// describing the antenna is half of what a ragchew is for. Without the
        /// digit ANT becomes 190. Every report an operator actually sends has a
        /// digit in it, because readability is sent as a figure: 5NN, 5TT, 4NN.
        /// A report of TTT would be missed, and a report of TTT does not exist.
        /// </summary>
        public static string ExpandCutNumbers(string word)
        {
            if (string.IsNullOrEmpty(word)) return word;

            bool anyCut = false, anyDigit = false;
            foreach (char c in word)
            {
                if (char.IsDigit(c)) { anyDigit = true; continue; }
                if (c == 'T' || c == 'N' || c == 'A') { anyCut = true; continue; }
                return word;
            }
            if (!anyCut || !anyDigit) return word;

            var sb = new StringBuilder(word.Length);
            foreach (char c in word)
                sb.Append(c == 'T' ? '0' : c == 'N' ? '9' : c == 'A' ? '1' : c);
            return sb.ToString();
        }

        private static bool LooksLikeCall(string w)
            => !NotCalls.Contains(w) && w.Any(char.IsDigit) && CallPattern.IsMatch(w);

        private static List<string> Words(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();

            // The decoder emits '?' for a symbol it could not decode and runs
            // procedural signals together as <BK>, neither of which is part of
            // a word. Splitting on everything that is not a letter, a digit or
            // a slash leaves the words and drops the rest.
            return Regex.Split(text.ToUpperInvariant(), "[^A-Z0-9/]+")
                        .Where(w => w.Length > 0)
                        .ToList();
        }
    }
}
