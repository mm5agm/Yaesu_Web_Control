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

        public Task<string?> ReadAsync(string command, CancellationToken cancellationToken) =>
            _mux.SendCommandAsync(command, ClientId, cancellationToken, ReadTimeoutMs);

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

        /// <summary>True after N1MM sent TX1/TX2 and before it sent TX0.</summary>
        public bool Keyed { get; private set; }

        /// <summary>The first command that wasn't Yaesu CAT, for one hint in the log.</summary>
        public string? FirstInvalid { get; private set; }

        public int Commands { get; private set; }

        /// <summary>
        /// Handles whatever text arrived and returns what to send back: the
        /// answers to its reads, each with its ';', in order. A partial
        /// command waits for the rest.
        /// </summary>
        public async Task<string> ProcessAsync(string text, CancellationToken cancellationToken)
        {
            _pending.Append(text);
            var reply = new StringBuilder();
            foreach (var command in N1mmCatProtocol.TakeCommands(_pending))
            {
                Commands++;
                switch (N1mmCatProtocol.Classify(command))
                {
                    case N1mmCatKind.Invalid:
                        FirstInvalid ??= command.Length > 40 ? command[..40] : command;
                        break;

                    case N1mmCatKind.AutoInfo:
                        if (command == "AI;") reply.Append(N1mmCatProtocol.AutoInfoOffReply);
                        break;

                    case N1mmCatKind.Read:
                        if (!_link.IsConnected) break;
                        var answer = await _link.ReadAsync(command, cancellationToken);
                        if (!string.IsNullOrEmpty(answer)) reply.Append(answer).Append(';');
                        break;

                    case N1mmCatKind.Set:
                        if (!_link.IsConnected) break;
                        await _link.WriteAsync(command, cancellationToken);
                        break;
                }

                if (N1mmCatProtocol.Keys(command) && _link.IsConnected) Keyed = true;
                else if (N1mmCatProtocol.Unkeys(command)) Keyed = false;
            }
            return reply.ToString();
        }

        /// <summary>Marks the session unkeyed after the server forced TX0.</summary>
        public void ClearKeyed() => Keyed = false;
    }
}
