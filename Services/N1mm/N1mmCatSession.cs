using System.Text;

namespace Yaesu_Web_Control.Services.N1mm
{
    /// <summary>
    /// The radio as the N1MM CAT server sees it. In the app this is the
    /// shared CAT multiplexer; tests use a fake.
    /// </summary>
    public interface ICatPassThrough
    {
        bool IsConnected { get; }

        /// <summary>Sends a read; returns the answer without its ';', or null if none came.</summary>
        Task<string?> ReadAsync(string command, CancellationToken cancellationToken);

        /// <summary>Sends a set and waits for nothing.</summary>
        Task WriteAsync(string command, CancellationToken cancellationToken);
    }

    /// <summary>The app's <see cref="ICatPassThrough"/>: the multiplexer that owns the COM port.</summary>
    public sealed class MultiplexerPassThrough : ICatPassThrough
    {
        public const string ClientId = "n1mm";

        // N1MM's own CAT time-out is 1000 ms or more; the radio answers a
        // read in a few ms, so this leaves room for a busy port.
        public const int ReadTimeoutMs = 300;

        private readonly CatMultiplexerService _mux;
        public MultiplexerPassThrough(CatMultiplexerService mux) => _mux = mux;

        public bool IsConnected => _mux.IsConnected;

        // The answer also goes to YWC's own state: when N1MM reads FA just
        // after the radio was tuned, it takes the reply YWC would have used.
        public Task<string?> ReadAsync(string command, CancellationToken cancellationToken) =>
            _mux.SendReadAndDispatchAsync(command, ClientId, cancellationToken, ReadTimeoutMs);

        public Task WriteAsync(string command, CancellationToken cancellationToken) =>
            _mux.SendWithoutReplyAsync(command, cancellationToken);
    }

    /// <summary>
    /// One N1MM connection: text in, the radio's answers out. Commands are
    /// handled one at a time, in the order N1MM sent them, as the radio
    /// itself would.
    /// </summary>
    public sealed class N1mmCatSession
    {
        private readonly ICatPassThrough _link;
        private readonly StringBuilder _pending = new();

        public N1mmCatSession(ICatPassThrough link) => _link = link;

        // A read the radio misses this many times in a row is set aside...
        public const int MissesBeforeSetAside = 2;
        // ...and not sent again for this long. Rick's FTdx101D never answered
        // N1MM's MG; in CW, and each one held the rest of the batch for the
        // full read time-out, so a band button's FA waited 15 s behind them
        // and N1MM put its old band back (#394, 2026-11-24).
        public static readonly TimeSpan SetAsideFor = TimeSpan.FromSeconds(30);

        /// <summary>The clock, so tests can move time on.</summary>
        public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

        private readonly Dictionary<string, (int Misses, DateTime RetryAt)> _misses = new();

        /// <summary>Reads not sent because the radio kept missing them.</summary>
        public int SetAside { get; private set; }

        /// <summary>Called once each time a read is set aside, for the log.</summary>
        public Action<string>? OnSetAside { get; set; }

        /// <summary>True after N1MM sent TX1/TX2 and before it sent TX0.</summary>
        public bool Keyed { get; private set; }

        /// <summary>The first command that wasn't Yaesu CAT, for one hint in the log.</summary>
        public string? FirstInvalid { get; private set; }

        public int Commands { get; private set; }

        /// <summary>Reads the radio didn't answer in time.</summary>
        public int Unanswered { get; private set; }

        /// <summary>Called with each read the radio didn't answer, for the log.</summary>
        public Action<string>? OnUnanswered { get; set; }

        /// <summary>Sets passed to the radio (frequency, mode, band, macros).</summary>
        public int Sets { get; private set; }

        /// <summary>Called with each set passed to the radio, for the log.</summary>
        public Action<string>? OnSet { get; set; }

        /// <summary>
        /// Handles whatever text arrived and returns what to send back: the
        /// answers to its reads, each with its ';', in order. A partial
        /// command waits for the rest.
        /// </summary>
        public async Task<string> ProcessAsync(string text, CancellationToken cancellationToken)
        {
            var reply = new StringBuilder();
            await ProcessAsync(text, (answer, _) => { reply.Append(answer); return Task.CompletedTask; }, cancellationToken);
            return reply.ToString();
        }

        /// <summary>
        /// As above, but each answer goes to <paramref name="send"/> as soon as
        /// the radio gives it, so N1MM isn't kept waiting for the rest of what
        /// it sent in the same packet (#394).
        /// </summary>
        public async Task ProcessAsync(string text, Func<string, CancellationToken, Task> send, CancellationToken cancellationToken)
        {
            _pending.Append(text);
            foreach (var command in N1mmCatProtocol.TakeCommands(_pending))
            {
                Commands++;
                switch (N1mmCatProtocol.Classify(command))
                {
                    case N1mmCatKind.Invalid:
                        FirstInvalid ??= command.Length > 40 ? command[..40] : command;
                        break;

                    case N1mmCatKind.AutoInfo:
                        if (command == "AI;") await send(N1mmCatProtocol.AutoInfoOffReply, cancellationToken);
                        break;

                    case N1mmCatKind.Read:
                        if (!_link.IsConnected) break;
                        if (_misses.TryGetValue(command, out var miss)
                            && miss.Misses >= MissesBeforeSetAside && Now() < miss.RetryAt)
                        {
                            // N1MM gets no answer, as it wouldn't from the
                            // radio, but without the wait.
                            SetAside++;
                            break;
                        }
                        var answer = await _link.ReadAsync(command, cancellationToken);
                        if (!string.IsNullOrEmpty(answer))
                        {
                            _misses.Remove(command);
                            await send(answer + ";", cancellationToken);
                        }
                        else
                        {
                            Unanswered++;
                            OnUnanswered?.Invoke(command);
                            int misses = _misses.TryGetValue(command, out var m) ? m.Misses + 1 : 1;
                            _misses[command] = (misses, Now() + SetAsideFor);
                            if (misses == MissesBeforeSetAside) OnSetAside?.Invoke(command);
                        }
                        break;

                    case N1mmCatKind.Set:
                        if (!_link.IsConnected) break;
                        Sets++;
                        OnSet?.Invoke(command);
                        await _link.WriteAsync(command, cancellationToken);
                        break;
                }

                if (N1mmCatProtocol.Keys(command) && _link.IsConnected) Keyed = true;
                else if (N1mmCatProtocol.Unkeys(command)) Keyed = false;
            }
        }

        /// <summary>Marks the session unkeyed after the server forced TX0.</summary>
        public void ClearKeyed() => Keyed = false;
    }
}
