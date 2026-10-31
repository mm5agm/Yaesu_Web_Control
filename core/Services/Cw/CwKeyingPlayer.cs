namespace RadioWebControl.Core.Services.Cw
{
    /// <summary>
    /// One key event from a keyer in the operator's browser: key down or up at
    /// sender time <see cref="T"/> (the browser's performance.now(), ms).
    /// Sequence numbers run on without gaps, so a lost event can be seen.
    /// </summary>
    public sealed class CwKeyEvent
    {
        public long Seq { get; set; }
        public bool Down { get; set; }
        public double T { get; set; }
    }

    /// <summary>
    /// What the browser sends: the latest few events (each packet repeats the
    /// ones before it, so one lost packet costs nothing) and its clock at the
    /// moment of sending. A packet with no events is a ping: the link is alive.
    /// <see cref="Abort"/> means let go of the key now.
    /// </summary>
    public sealed class CwKeyPacket
    {
        public double Now { get; set; }
        public List<CwKeyEvent> Events { get; set; } = new();
        public bool Abort { get; set; }
    }

    /// <summary>The thing that actually keys: a serial line, or a dry-run log.</summary>
    public interface ICwKeyLine
    {
        void SetKey(bool down);
    }

    public sealed class CwKeyingOptions
    {
        /// <summary>
        /// How long events wait before being played, on top of the fastest
        /// delivery seen. Absorbs network jitter: an event delayed by less
        /// than this still plays on time.
        /// </summary>
        public double BufferMs { get; set; } = 150;

        /// <summary>Key down and nothing heard from the browser for this long: let go.</summary>
        public double LinkLostMs { get; set; } = 800;

        /// <summary>
        /// Key down for longer than this is never Morse from an iambic keyer
        /// (a dah at 5 WPM is 720 ms): let go.
        /// </summary>
        public double MaxKeyDownMs { get; set; } = 3000;

        /// <summary>
        /// The clock offset is the smallest (arrival - sent) over this window,
        /// so a burst of slow packets does not move the timeline, and drift
        /// between the two PCs' clocks is followed slowly.
        /// </summary>
        public double OffsetWindowMs { get; set; } = 10000;

        /// <summary>
        /// Within a character the browser's spacing is kept exactly, even when
        /// that runs a little behind the buffer. A gap longer than this (a word
        /// space, a pause) lets the timeline catch up with the buffer again.
        /// </summary>
        public double RealignGapMs { get; set; } = 500;
    }

    /// <summary>
    /// A line for the log, and for the operator's page. A key up also carries
    /// the element's length as played and as sent, for the page's summary.
    /// </summary>
    public sealed record CwKeyingReport(string Kind, string Text, double AtMs, double? LateMs = null,
                                        double? PlayedMs = null, double? SentMs = null);

    /// <summary>
    /// Plays key events from a browser keyer on the shack PC, after a fixed
    /// jitter buffer, and lets go of the key whenever anything is wrong.
    ///
    /// Pure logic: the caller gives the time (local ms, any monotonic base) to
    /// <see cref="Receive"/> and <see cref="Tick"/>, and calls Tick when
    /// <see cref="NextDueMs"/> comes round. Thread-safety is the caller's.
    ///
    /// Timing: an event sent at browser time T plays at T + offset + buffer,
    /// where offset is the smallest (arrival - sent) recently seen - the
    /// fastest the network has delivered. A key up always plays exactly the
    /// element's length after its key down actually played, so a late packet
    /// moves an element but never shortens it.
    ///
    /// Safety: the key is let go when the link goes quiet while it is down,
    /// when it has been down too long, when events are lost, and on abort.
    /// After a loss nothing plays until the next key down.
    /// </summary>
    public sealed class CwKeyingPlayer
    {
        private sealed class Pending
        {
            public required CwKeyEvent Event;
            public double PlayAt;
        }

        private readonly ICwKeyLine _line;
        private readonly CwKeyingOptions _o;
        private readonly LinkedList<Pending> _queue = new();
        private readonly Queue<(double At, double Offset)> _offsets = new();
        private long _lastSeq = -1;
        private bool _waitForDown;
        private double _lastPacketAt = double.NegativeInfinity;
        private double _keyDownAt;
        // The last event queued, for keeping the spacing between events.
        private CwKeyEvent? _prevEvent;
        private double _prevPlayAt;
        // The last key down played, for the length of the up that follows.
        private CwKeyEvent? _downPlayed;
        private double _downPlayedAt;

        public CwKeyingPlayer(ICwKeyLine line, CwKeyingOptions? options = null)
        {
            _line = line;
            _o = options ?? new CwKeyingOptions();
        }

        public event Action<CwKeyingReport>? Report;

        public bool KeyDown { get; private set; }

        /// <summary>The fastest (arrival - sent) in the window; null before any packet.</summary>
        public double? OffsetMs { get; private set; }

        /// <summary>When Tick next has something to do: an event, or a safety check.</summary>
        public double? NextDueMs
        {
            get
            {
                double? due = _queue.First?.Value.PlayAt;
                if (KeyDown)
                {
                    double safety = Math.Min(_keyDownAt + _o.MaxKeyDownMs, _lastPacketAt + _o.LinkLostMs);
                    due = due is null ? safety : Math.Min(due.Value, safety);
                }
                return due;
            }
        }

        public void Receive(CwKeyPacket packet, double now)
        {
            _lastPacketAt = now;
            if (packet.Abort)
            {
                Abort("the browser stopped the keyer", now);
                return;
            }
            UpdateOffset(now - packet.Now, now);

            foreach (var e in packet.Events.Where(e => e.Seq > _lastSeq).OrderBy(e => e.Seq))
            {
                if (_lastSeq >= 0 && e.Seq != _lastSeq + 1)
                {
                    long lost = e.Seq - _lastSeq - 1;
                    Abort($"{lost} key event{(lost == 1 ? "" : "s")} lost on the way", now);
                }
                _lastSeq = e.Seq;
                if (_waitForDown)
                {
                    // Start again at a key down that can still play on time:
                    // older ones (repeats that arrived after a loss) would
                    // play back to back with their gaps squeezed out.
                    if (!e.Down || e.T + OffsetMs!.Value + _o.BufferMs < now) continue;
                    _waitForDown = false;
                }
                Enqueue(e);
            }
            Tick(now);
        }

        public void Tick(double now)
        {
            while (_queue.First is { } node && node.Value.PlayAt <= now)
            {
                _queue.RemoveFirst();
                Play(node.Value, now);
            }
            if (!KeyDown) return;
            if (now - _lastPacketAt >= _o.LinkLostMs)
                Abort($"nothing from the browser for {now - _lastPacketAt:0} ms with the key down", now);
            else if (now - _keyDownAt >= _o.MaxKeyDownMs)
                Abort($"key down for {now - _keyDownAt:0} ms", now);
        }

        /// <summary>Lets go of the key, drops everything queued, and waits for the next key down.</summary>
        public void Abort(string reason, double now)
        {
            bool wasDown = KeyDown;
            SetKey(false, now);
            _queue.Clear();
            _waitForDown = true;
            _prevEvent = null;
            _downPlayed = null;
            Report?.Invoke(new CwKeyingReport("safety", wasDown ? $"Key let go: {reason}" : $"Stopped: {reason}", now));
        }

        /// <summary>
        /// Starts afresh for a new sender: lets go, and forgets its sequence
        /// numbers and clock offset. A different browser numbers its events
        /// from 0 on a clock with its own base, so without this its events
        /// would all look like repeats, or play at the wrong moment.
        /// </summary>
        public void Reset(double now)
        {
            SetKey(false, now);
            _queue.Clear();
            _offsets.Clear();
            OffsetMs = null;
            _lastSeq = -1;
            _waitForDown = false;
            _prevEvent = null;
            _downPlayed = null;
        }

        private void UpdateOffset(double offset, double now)
        {
            _offsets.Enqueue((now, offset));
            while (_offsets.Count > 1 && now - _offsets.Peek().At > _o.OffsetWindowMs) _offsets.Dequeue();
            OffsetMs = _offsets.Min(x => x.Offset);
        }

        private void Enqueue(CwKeyEvent e)
        {
            double playAt = e.T + OffsetMs!.Value + _o.BufferMs;
            if (_prevEvent is not null && e.T - _prevEvent.T <= _o.RealignGapMs)
                playAt = Math.Max(playAt, _prevPlayAt + (e.T - _prevEvent.T));
            // An up whose down has already played (it came in a later packet)
            // still gets the element's full length.
            if (!e.Down && _downPlayed is not null && ReferenceEquals(_prevEvent, _downPlayed))
                playAt = Math.Max(playAt, _downPlayedAt + (e.T - _downPlayed.T));
            _prevEvent = e;
            _prevPlayAt = playAt;
            _queue.AddLast(new Pending { Event = e, PlayAt = playAt });
        }

        private void Play(Pending p, double now)
        {
            var e = p.Event;
            double late = now - p.PlayAt;
            if (e.Down)
            {
                SetKey(true, now);
                _downPlayed = e;
                _downPlayedAt = now;
                // The up that follows plays one element length after this,
                // however late this was.
                if (_queue.First is { } next && !next.Value.Event.Down)
                    next.Value.PlayAt = Math.Max(next.Value.PlayAt, now + (next.Value.Event.T - e.T));
            }
            else
            {
                SetKey(false, now);
            }
            string what = e.Down ? "down" : "up  ";
            double? played = null, sent = null;
            if (!e.Down && _downPlayed is not null)
            {
                played = now - _downPlayedAt;
                sent = e.T - _downPlayed.T;
            }
            string len = played is null ? "" : $", {played:0.0} ms (sent {sent:0.0})";
            Report?.Invoke(new CwKeyingReport("key", $"key {what} #{e.Seq}{len}", now, late, played, sent));
        }

        private void SetKey(bool down, double now)
        {
            if (KeyDown == down) return;
            KeyDown = down;
            if (down) _keyDownAt = now;
            _line.SetKey(down);
        }
    }
}
