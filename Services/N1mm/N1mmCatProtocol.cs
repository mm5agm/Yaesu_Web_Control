using System.Text;
using System.Text.RegularExpressions;

namespace Yaesu_Web_Control.Services.N1mm
{
    /// <summary>
    /// What the N1MM CAT server does with one command from N1MM Logger+.
    /// </summary>
    public enum N1mmCatKind
    {
        /// <summary>Not Yaesu CAT text. Dropped, and logged once.</summary>
        Invalid,
        /// <summary>AI (auto information). Answered here, never sent to the radio.</summary>
        AutoInfo,
        /// <summary>A read: sent to the radio and its answer returned to N1MM.</summary>
        Read,
        /// <summary>A set: sent to the radio, nothing waited for.</summary>
        Set,
    }

    /// <summary>
    /// The text side of the N1MM CAT server (N1MM integration, mm5agm memory
    /// n1mm-logger-integration-todo; YWC #394). N1MM Logger+ connects over TCP
    /// (Configure Ports, Port = TCP, "IP Addr:Port", since N1MM 1.0.9429) with
    /// its radio set to the real Yaesu model, and sends the radio's own CAT
    /// text. Yaesu Web Control holds the COM port, so it passes each command
    /// on and returns the radio's answer. Pure functions, so the rules are
    /// unit-tested without a socket or a radio.
    /// </summary>
    public static class N1mmCatProtocol
    {
        /// <summary>The longest Yaesu CAT command accepted, with its ';'.</summary>
        public const int MaxCommandLength = 64;

        /// <summary>Text with no ';' beyond this is thrown away as garbage.</summary>
        public const int MaxPendingLength = 512;

        /// <summary>The answer to N1MM's AI read. See <see cref="Classify"/>.</summary>
        public const string AutoInfoOffReply = "AI0;";

        // Two capital letters, then the parameters Yaesu CAT uses: digits,
        // hex letters and mode codes, signs, and the spaces of a few text
        // replies. Nothing that could be a line of another protocol.
        private static readonly Regex Command = new(@"^[A-Z]{2}[A-Za-z0-9+\-. ]{0,61};$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Takes every complete command (up to and including ';') out of
        /// <paramref name="pending"/>, leaving a partial one at the end for
        /// the next read. CR, LF and spaces between commands are ignored.
        /// </summary>
        public static List<string> TakeCommands(StringBuilder pending)
        {
            var commands = new List<string>();
            var text = pending.ToString();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != ';') continue;
                var cmd = text.Substring(start, i - start + 1).Trim(' ', '\r', '\n', '\t');
                if (cmd.Length > 1) commands.Add(cmd);
                start = i + 1;
            }
            pending.Clear();
            var rest = text.Substring(start).TrimStart(' ', '\r', '\n', '\t');
            if (rest.Length <= MaxPendingLength) pending.Append(rest);
            return commands;
        }

        /// <summary>
        /// Sorts a command. A read is the two letters alone ("FA;", "IF;",
        /// "TX;") or with one selector character ("MD0;", "SM0;", "RM5;"):
        /// the same rule as the five-byte translator's LooksLikeRead, except
        /// the known one-character sets ("TX1;", "BD0;"). Any other
        /// one-character set falls in with the reads; the radio gives it no
        /// answer, so it costs a short wait and nothing is returned, which is
        /// what the real radio would do. EX menu reads
        /// carry a six-digit menu number.
        ///
        /// AI is never passed on. Yaesu Web Control needs the radio's auto
        /// information on (it is how it hears frequency changes), and "AI0;"
        /// from N1MM would turn it off. N1MM is told AI is off, so it polls,
        /// which is what it does by default.
        /// </summary>
        public static N1mmCatKind Classify(string command)
        {
            if (command.Length > MaxCommandLength || !Command.IsMatch(command))
                return N1mmCatKind.Invalid;
            if (command.StartsWith("AI", StringComparison.Ordinal))
                return N1mmCatKind.AutoInfo;
            var bodyLength = command.Length - 3;
            if (bodyLength == 1 && OneCharacterSets.Contains(command[..2])) return N1mmCatKind.Set;
            if (bodyLength <= 1) return N1mmCatKind.Read;
            if (command.StartsWith("EX", StringComparison.Ordinal) && bodyLength == 6) return N1mmCatKind.Read;
            return N1mmCatKind.Set;
        }

        // Sets whose whole parameter is one character, so they aren't held
        // for an answer: TX0/1/2 above all, so N1MM's next command after
        // keying isn't delayed. Their reads are the bare letters ("TX;").
        private static readonly HashSet<string> OneCharacterSets = new(StringComparer.Ordinal)
        {
            "TX", "FT", "VX", "BD", "BU",
        };

        /// <summary>True for the commands that key the transmitter by CAT.</summary>
        public static bool Keys(string command) => command is "TX1;" or "TX2;";

        /// <summary>True for the command that returns the radio to receive.</summary>
        public static bool Unkeys(string command) => command == "TX0;";
    }
}
