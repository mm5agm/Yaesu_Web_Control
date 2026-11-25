using System.Text.RegularExpressions;

namespace Yaesu_Web_Control.Services.Wsjtx
{
    /// <summary>Why a station is worth calling.</summary>
    public enum CallKind
    {
        /// <summary>The station is calling the operator.</summary>
        CallingYou,
        /// <summary>CQ or QRZ.</summary>
        Cq,
        /// <summary>Just sent RR73, RRR or 73, so it has finished a QSO.</summary>
        Finished,
    }

    public sealed record CallListEntry(
        string Call,
        CallKind Kind,
        string Grid,
        WsjtxDecode Decode,
        DateTime ReceivedUtc,
        long FirstSeenOrder);

    /// <summary>
    /// The stations the big-tile page offers to call, built from WSJT-X/WSJT-Z
    /// decodes: stations calling CQ or QRZ, stations that have just finished a
    /// QSO, and stations calling the operator. Each station appears once,
    /// holding its latest decode, which is the one a Reply must echo.
    /// A station that starts a QSO with someone else drops off, and so does
    /// one not heard for two T/R periods. Thread-safe.
    /// </summary>
    public sealed class WsjtxCallList
    {
        private static readonly Regex CallsignPattern =
            new(@"^(?=.*[0-9])(?=.*[A-Z])[A-Z0-9/]{3,13}$", RegexOptions.Compiled);
        private static readonly Regex GridPattern =
            new(@"^[A-R]{2}[0-9]{2}$", RegexOptions.Compiled);

        private readonly object _lock = new();
        private readonly Dictionary<string, CallListEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private long _order;

        /// <summary>The operator's own callsign (WSJT-X's "DE call").</summary>
        public string MyCall { get; set; } = "";

        public void Clear()
        {
            lock (_lock) _entries.Clear();
        }

        public void Add(WsjtxDecode decode, DateTime nowUtc)
        {
            var parsed = Classify(decode.Message, MyCall);
            lock (_lock)
            {
                if (parsed is null) return;
                var (call, kind, grid, busy) = parsed.Value;
                if (busy)
                {
                    // The station is now in a QSO with someone else.
                    _entries.Remove(call);
                    return;
                }
                long order = _entries.TryGetValue(call, out var old) ? old.FirstSeenOrder : ++_order;
                if (grid.Length == 0 && old is not null) grid = old.Grid;
                _entries[call] = new CallListEntry(call, kind, grid, decode, nowUtc, order);
            }
        }

        public bool TryGet(string call, out CallListEntry? entry)
        {
            lock (_lock) return _entries.TryGetValue(call, out entry);
        }

        /// <summary>
        /// Stations heard within maxAge, oldest arrival first so a tile keeps
        /// its place, with stations calling the operator ahead of the rest.
        /// Older entries are dropped.
        /// </summary>
        public IReadOnlyList<CallListEntry> Snapshot(DateTime nowUtc, TimeSpan maxAge)
        {
            lock (_lock)
            {
                foreach (var key in _entries.Where(e => nowUtc - e.Value.ReceivedUtc > maxAge)
                                            .Select(e => e.Key).ToList())
                    _entries.Remove(key);
                return _entries.Values
                    .OrderBy(e => e.Kind == CallKind.CallingYou ? 0 : 1)
                    .ThenBy(e => e.FirstSeenOrder)
                    .ToList();
            }
        }

        /// <summary>The T/R period in seconds for a WSJT-X mode name.</summary>
        public static double PeriodSeconds(string mode) => mode.ToUpperInvariant() switch
        {
            "FT4" => 7.5,
            "FT2" => 3.8,
            _ => 15,
        };

        /// <summary>
        /// How long to wait until leadSeconds after the start of a T/R period.
        /// Periods start on whole multiples of the period, counted in UTC.
        /// Call CQ waits for this so the first CQ goes out in full: WSJT-X starts
        /// at once if Enable Tx comes on part-way through its own period, and a
        /// few seconds of FT8 can't be decoded.
        /// </summary>
        public static TimeSpan DelayToPeriodStart(DateTime utcNow, double periodSeconds, double leadSeconds)
        {
            double t = (utcNow - DateTime.UnixEpoch).TotalSeconds;
            double target = Math.Floor(t / periodSeconds) * periodSeconds + leadSeconds;
            if (target <= t) target += periodSeconds;
            return TimeSpan.FromSeconds(target - t);
        }

        /// <summary>
        /// Works out who sent a message and whether they can be called.
        /// Returns null for messages that say nothing useful about a station.
        /// busy=true means the sender is in a QSO with someone else.
        /// </summary>
        internal static (string Call, CallKind Kind, string Grid, bool Busy)? Classify(string message, string myCall)
        {
            var t = message.ToUpperInvariant()
                           .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 2) return null;
            myCall = myCall.ToUpperInvariant();

            if (t[0] == "CQ" || t[0] == "QRZ")
            {
                // "CQ K1ABC FN42", "CQ DX K1ABC FN42", "CQ POTA K1ABC", "CQ 290 K1ABC FN42"
                int i = 1;
                if (t.Length >= 3 && !IsCallsign(t[1]) && IsCallsign(Unhash(t[2]))) i = 2;
                var call = Unhash(t[i]);
                if (!IsCallsign(call) || call == myCall) return null;
                string grid = t.Length > i + 1 && GridPattern.IsMatch(t[i + 1]) ? t[i + 1] : "";
                return (call, CallKind.Cq, grid, false);
            }

            var to = Unhash(t[0]);
            var from = Unhash(t[1]);
            if (!IsCallsign(from) || from == myCall) return null;

            if (myCall.Length > 0 && to == myCall)
            {
                string grid = t.Length > 2 && GridPattern.IsMatch(t[2]) ? t[2] : "";
                return (from, CallKind.CallingYou, grid, false);
            }

            var last = t[^1];
            if (t.Length >= 3 && (last == "RR73" || last == "73" || last == "RRR"))
                return (from, CallKind.Finished, "", false);

            // "W1XYZ K1ABC FN42", "W1XYZ K1ABC -10", "W1XYZ K1ABC R-10": K1ABC is busy.
            return (from, CallKind.Cq, "", true);
        }

        private static string Unhash(string token) =>
            token.Length > 2 && token[0] == '<' && token[^1] == '>' ? token[1..^1] : token;

        private static bool IsCallsign(string token) =>
            token is not ("RR73" or "RRR" or "73") && CallsignPattern.IsMatch(token) && !GridPattern.IsMatch(token)
            && !(token.Length >= 2 && (token[0] == 'R' && (token[1] == '-' || token[1] == '+')));
    }
}
