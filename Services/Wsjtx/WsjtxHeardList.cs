using System.Text.RegularExpressions;

namespace Yaesu_Web_Control.Services.Wsjtx
{
    /// <summary>One station WSJT-X/WS decoded, for the Spot Map's "I heard".</summary>
    public sealed record HeardStation(
        long Seq,
        string Call,
        string Grid,
        long UnixSeconds,
        long FrequencyHz,
        string Mode,
        int Snr,
        double DeltaTime);

    /// <summary>
    /// Every station WSJT-X/WS decodes, kept for the Spot Map so "I heard" shows
    /// up a second or two after each period instead of waiting for PSK Reporter:
    /// WSJT-X uploads to PSK Reporter in batches a few minutes apart.
    /// A station needs a grid to go on the map. Its own message gives one only
    /// in a CQ or a first reply ("K1ABC W9XYZ EN52"), so grids are remembered
    /// for the session, and a station heard before its grid is known is held
    /// back until a later message gives it. Thread-safe.
    /// </summary>
    public sealed class WsjtxHeardList
    {
        private static readonly Regex GridPattern = new(@"^[A-R]{2}[0-9]{2}([A-X]{2})?$", RegexOptions.Compiled);
        private const int MaxItems = 3000;
        private const int MaxGrids = 20000;
        private static readonly TimeSpan Keep = TimeSpan.FromHours(1);

        private readonly object _lock = new();
        private readonly List<HeardStation> _items = new();
        private readonly Dictionary<string, string> _grids = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HeardStation> _waiting = new(StringComparer.OrdinalIgnoreCase);
        private long _seq;

        /// <summary>
        /// Adds one decode. dialHz is WSJT-X's dial frequency; the decode's audio
        /// offset is added to it. Own messages, replays and decodes from a file
        /// are left out.
        /// </summary>
        public void Add(WsjtxDecode decode, long dialHz, string statusMode, string myCall, DateTime nowUtc)
        {
            if (!decode.IsNew || decode.OffAir) return;
            var sender = Sender(decode.Message, myCall);
            if (sender is null) return;
            var (call, grid) = sender.Value;
            long unix = (long)(nowUtc - DateTime.UnixEpoch).TotalSeconds;
            long hz = dialHz > 0 ? dialHz + decode.DeltaFrequency : 0;
            string mode = WsjtxUdpService.DecodeModeName(decode.Mode, statusMode);
            lock (_lock)
            {
                if (grid.Length > 0)
                {
                    if (_grids.Count >= MaxGrids) _grids.Clear();
                    _grids[call] = grid;
                }
                else _grids.TryGetValue(call, out grid!);
                grid ??= "";
                var heard = new HeardStation(0, call, grid, unix, hz, mode, decode.Snr, decode.DeltaTime);
                if (grid.Length == 0)
                {
                    _waiting[call] = heard;
                    return;
                }
                _waiting.Remove(call);   // this newer decode stands in for it
                Publish(heard);
                Trim(nowUtc);
            }
        }

        private void Publish(HeardStation h)
        {
            _items.Add(h with { Seq = ++_seq });
        }

        private void Trim(DateTime nowUtc)
        {
            long oldest = (long)(nowUtc - Keep - DateTime.UnixEpoch).TotalSeconds;
            int drop = 0;
            while (drop < _items.Count && (_items[drop].UnixSeconds < oldest || _items.Count - drop > MaxItems)) drop++;
            if (drop > 0) _items.RemoveRange(0, drop);
            foreach (var key in _waiting.Where(w => w.Value.UnixSeconds < oldest).Select(w => w.Key).ToList())
                _waiting.Remove(key);
        }

        /// <summary>Stations added after the given sequence number, oldest first.</summary>
        public (long Seq, IReadOnlyList<HeardStation> Items) Since(long seq)
        {
            lock (_lock)
                return (_seq, _items.Where(i => i.Seq > seq).ToList());
        }

        public void Clear()
        {
            lock (_lock) { _items.Clear(); _waiting.Clear(); }
        }

        /// <summary>
        /// Who sent a message, and their grid if the message gives one.
        /// "CQ K1ABC FN42", "CQ DX K1ABC FN42", "W9XYZ K1ABC FN42", "W9XYZ K1ABC R FN42",
        /// "W9XYZ K1ABC -10", "W9XYZ K1ABC RR73": all sent by K1ABC.
        /// Null for free text and for the operator's own messages.
        /// </summary>
        internal static (string Call, string Grid)? Sender(string message, string myCall)
        {
            var t = message.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 2) return null;
            myCall = (myCall ?? "").ToUpperInvariant();
            int i;
            if (t[0] == "CQ" || t[0] == "QRZ")
                i = t.Length >= 3 && !WsjtxCallList.IsCallsign(t[1]) && WsjtxCallList.IsCallsign(WsjtxCallList.Unhash(t[2])) ? 2 : 1;
            else
                i = 1;
            var call = WsjtxCallList.Unhash(t[i]);
            if (!WsjtxCallList.IsCallsign(call) || call == myCall) return null;
            int g = i + 1;
            if (g < t.Length && t[g] == "R") g++;
            string grid = g < t.Length && t[g] != "RR73" && GridPattern.IsMatch(t[g]) ? t[g][..4] : "";
            return (call, grid);
        }
    }
}
