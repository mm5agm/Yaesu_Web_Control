using System.Text.RegularExpressions;

namespace Yaesu_Web_Control.Services.Rtty
{
    /// <summary>
    /// The operator's own RTTY setup: CAT commands, typed in Settings, that the
    /// CAT SET key on RTTY Contest sends to the radio - filters, scope, menus,
    /// whatever their station wants for RTTY. Bruce VK2RT, Discussion #250,
    /// 2026-10-30, who already keeps such a string for 20 m.
    ///
    /// <para>One line per set of commands. A line may start with a band and a
    /// colon ("20m: FA014090000;"): it is then sent only for that band, after
    /// the lines with no band, which are sent for every band.</para>
    ///
    /// <para><b>Nothing here may make the radio transmit, or write over
    /// anything the operator keeps in it.</b> A command that keys the
    /// transmitter, starts a tune, plays or records a message, writes a
    /// memory or switches the radio off refuses the whole setup, with the
    /// commands named, rather than being skipped: half a setup sent is a radio
    /// in a state nobody chose.</para>
    /// </summary>
    public static class RttySetupCat
    {
        // TX/MX/VX key the transmitter; KY sends CW and KM writes the keyer's
        // memories; PB plays and LM records a voice message; AC starts the ATU
        // tune; AM/BM/MW/MT/QI write memories; PS switches the radio off.
        public static readonly IReadOnlySet<string> Refused = new HashSet<string>(StringComparer.Ordinal)
        {
            "TX", "MX", "VX", "KY", "KM", "PB", "LM", "AC", "AM", "BM", "MW", "MT", "QI", "PS",
        };

        private static readonly Regex BandPrefix = new(@"^\s*(\d+(?:\.\d+)?c?m)\s*:", RegexOptions.IgnoreCase);
        private static readonly Regex Command = new(@"^[A-Z]{2}[A-Z0-9+\-. ]*$");

        /// <summary>
        /// The commands to send for <paramref name="band"/> (e.g. "20m"; null
        /// for none), each without its ";", and what is wrong with the setup.
        /// The commands are only to be sent when the problems are empty.
        /// </summary>
        public static (IReadOnlyList<string> Commands, IReadOnlyList<string> Problems) For(string? text, string? band)
        {
            var common = new List<string>();
            var banded = new List<string>();
            var problems = new List<string>();

            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                string? lineBand = null;
                var m = BandPrefix.Match(line);
                if (m.Success)
                {
                    lineBand = m.Groups[1].Value.ToLowerInvariant();
                    line = line[m.Length..];
                }

                foreach (var part in line.Split(';'))
                {
                    var cmd = part.Trim().ToUpperInvariant();
                    if (cmd.Length == 0) continue;
                    if (!Command.IsMatch(cmd))
                        problems.Add($"\"{part.Trim()}\" is not a CAT command");
                    else if (Refused.Contains(cmd[..2]))
                        problems.Add($"{cmd}; is not allowed: it could transmit or write over the radio's memories");
                    else if (lineBand is null)
                        common.Add(cmd);
                    else if (band is not null && string.Equals(lineBand, band, StringComparison.OrdinalIgnoreCase))
                        banded.Add(cmd);
                }
            }

            common.AddRange(banded);
            return (common, problems);
        }
    }
}
