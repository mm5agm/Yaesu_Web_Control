using System;
using System.Collections.Generic;

namespace RadioWebControl.Core.Services.Rtty
{
    /// <summary>
    /// Who is watching the tuning scope. Each browser window showing the
    /// figure holds a lease under its own id, and the audio stays open while
    /// any lease does.
    ///
    /// Before this there was one flag for the whole host, so with the tuner
    /// open in two windows - the main page and a pop-out, or two PCs -
    /// closing either one stopped the figure in both. Now a stop lets go of
    /// that window's lease only.
    ///
    /// A lease ends one of two ways:
    ///
    ///   stopped   the window said it had closed, and has not started again
    ///             within <see cref="StopDebounce"/>. Closing and reopening
    ///             the dialog must not close and reopen the radio's USB
    ///             capture, which has native-crashed the host on that codec.
    ///   idle      the window has not polled for <see cref="IdleStop"/>: it
    ///             has gone without saying so - the tab closed, the PC slept.
    ///
    /// Pure and clocked from outside, so it can be tested without waiting.
    /// Not thread-safe: the owner holds its own lock round every call.
    /// </summary>
    public sealed class RttyTunerLeases
    {
        public static readonly TimeSpan IdleStop     = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan StopDebounce = TimeSpan.FromSeconds(2);

        private sealed class Lease
        {
            public DateTime LastSeenUtc;
            public DateTime? StopRequestedUtc;
        }

        private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);

        public int Count => _leases.Count;

        public bool Holds(string client) => _leases.ContainsKey(Key(client));

        /// <summary>The window has opened the figure, or changed its settings.</summary>
        public void Start(string? client, DateTime nowUtc)
        {
            var key = Key(client);
            if (!_leases.TryGetValue(key, out var lease)) _leases[key] = lease = new Lease();
            lease.LastSeenUtc = nowUtc;
            lease.StopRequestedUtc = null;
        }

        /// <summary>
        /// The window asked for a sweep. Returns whether it holds a lease: a
        /// window that does not - its lease went idle while its tab was in
        /// the background - has to start again, and a poll alone must not
        /// give it one, or a request still in flight when the window closed
        /// would bring the lease back.
        /// </summary>
        public bool Poll(string? client, DateTime nowUtc)
        {
            if (!_leases.TryGetValue(Key(client), out var lease)) return false;
            lease.LastSeenUtc = nowUtc;
            return true;
        }

        /// <summary>The window has closed. Takes effect after <see cref="StopDebounce"/> unless it starts again.</summary>
        public void Stop(string? client, DateTime nowUtc)
        {
            if (_leases.TryGetValue(Key(client), out var lease))
                lease.StopRequestedUtc ??= nowUtc;
        }

        /// <summary>
        /// Drops the leases that have ended. Returns why the last one went
        /// when that leaves none, or null while any is still held - including
        /// when there were none to begin with, which is the owner's business.
        /// </summary>
        public string? Expire(DateTime nowUtc)
        {
            if (_leases.Count == 0) return null;
            string? why = null;
            List<string>? ended = null;
            foreach (var (key, lease) in _leases)
            {
                string? w = lease.StopRequestedUtc is { } at && nowUtc - at >= StopDebounce ? "dialog closed"
                          : nowUtc - lease.LastSeenUtc > IdleStop ? "no page polling"
                          : null;
                if (w == null) continue;
                (ended ??= new()).Add(key);
                why = w;
            }
            if (ended == null) return null;
            foreach (var key in ended) _leases.Remove(key);
            return _leases.Count == 0 ? why : null;
        }

        public void Clear() => _leases.Clear();

        // A caller that sends no id - an older page - shares one lease, which
        // is how the host behaved for everyone before.
        private static string Key(string? client) => client ?? "";
    }
}
