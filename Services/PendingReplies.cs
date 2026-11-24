using System.Collections.Concurrent;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// The reads waiting for the radio's answer, one slot per reply key (see
    /// <see cref="CatMultiplexerService.ReplyKey"/>).
    ///
    /// Two things went wrong when this was a plain dictionary (Rick W2JAZ,
    /// #394: N1MM's band changes slow or lost, "Click to Reset Radio"):
    /// a read that timed out removed whatever was in its slot, so a newer read
    /// with the same letters lost its answer as well; and two clients asking
    /// the same thing at once (YWC's meter loop and N1MM both send "TX;")
    /// sent it twice and took each other's slot.
    ///
    /// Now a read only ever removes its own waiter, and a pure read (the key
    /// and ';', such as "TX;" or "SM0;") that's already waiting for an answer
    /// isn't sent again: the second asker shares the first one's answer. Sets
    /// and anything with parameters are always sent.
    ///
    /// A read never shares an answer asked for before the last set (see
    /// <see cref="NoteSet"/>): after N1MM's band button sends "FA007025000;",
    /// its next "FA;" must not take the answer to a "FA;" that went out
    /// before the set, which would carry the old band (#394).
    /// </summary>
    public sealed class PendingReplies
    {
        private sealed class Waiter
        {
            public Waiter(string command, long generation) { Command = command; Generation = generation; }
            public string Command { get; }
            public long Generation { get; }
            public TaskCompletionSource<string> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly ConcurrentDictionary<string, Waiter> _slots = new();
        private long _generation;

        /// <summary>Call when a set goes to the radio: reads asked before it are no longer shared.</summary>
        public void NoteSet() => Interlocked.Increment(ref _generation);

        /// <summary>True for a read that asks for the key's value and nothing else.</summary>
        public static bool IsPlainRead(string key, string command) =>
            command.TrimEnd(';') == key;

        /// <summary>
        /// Waits up to <paramref name="timeoutMs"/> for the answer to
        /// <paramref name="command"/>, calling <paramref name="send"/> unless
        /// the same plain read is already waiting. Returns the answer without
        /// its ';', or null if none came.
        /// </summary>
        public async Task<string?> AskAsync(string key, string command, Func<Task> send, int timeoutMs, CancellationToken cancellationToken)
        {
            var mine = new Waiter(command, Interlocked.Read(ref _generation));
            Waiter waiter;
            while (true)
            {
                if (_slots.TryGetValue(key, out var existing))
                {
                    if (existing.Command == command && IsPlainRead(key, command) && existing.Generation == mine.Generation)
                    {
                        waiter = existing;
                        break;
                    }
                    if (_slots.TryUpdate(key, mine, existing))
                    {
                        waiter = mine;
                        break;
                    }
                }
                else if (_slots.TryAdd(key, mine))
                {
                    waiter = mine;
                    break;
                }
            }

            try
            {
                if (waiter == mine) await send();
                var timeout = Task.Delay(timeoutMs, cancellationToken);
                var done = await Task.WhenAny(waiter.Answer.Task, timeout);
                return done == waiter.Answer.Task ? await waiter.Answer.Task : null;
            }
            finally
            {
                if (waiter == mine) _slots.TryRemove(new KeyValuePair<string, Waiter>(key, mine));
            }
        }

        /// <summary>Hands a reply to the read waiting for it; false if none is.</summary>
        public bool TryAnswer(string key, string reply)
        {
            if (!_slots.TryRemove(key, out var waiter)) return false;
            waiter.Answer.TrySetResult(reply);
            return true;
        }
    }
}
