namespace Yaesu_Web_Control.Services.Voice
{
    /// <summary>
    /// Commands that put the radio on the air need a much clearer match than the
    /// rest. The normal gate is low because a quiet or distant mic scores real
    /// commands low, and a misheard "noise reduction on" does little harm. A
    /// misheard transmit is a different matter: the recogniser always picks its
    /// closest phrase, and wrong phrases have been seen scoring 0.62.
    ///
    /// Commands that stop transmitting are deliberately not covered, so getting
    /// off the air stays as easy as it was.
    /// </summary>
    public static class VoiceTransmitGate
    {
        public const float MinConfidence = 0.85f;

        /// <summary>True if this command can start a transmission.</summary>
        public static bool StartsTransmit(string intent, string? macroCat) => intent switch
        {
            "TxOn" or "AtuTune" => true,
            "Macro" => MacroStartsTransmit(macroCat),
            _ => false,
        };

        private static bool MacroStartsTransmit(string? cat)
        {
            if (string.IsNullOrWhiteSpace(cat)) return false;
            return cat.Split(';', StringSplitOptions.RemoveEmptyEntries)
                      .Any(c => CommandStartsTransmit(c.Trim().ToUpperInvariant()));
        }

        private static bool CommandStartsTransmit(string c)
        {
            // TX1 and TX2 key the radio, TX0 is receive and TX alone only reads it.
            if (c.StartsWith("TX", StringComparison.Ordinal))
                return c.Length > 2 && c[2] != '0';
            // MX1: MOX on.
            if (c == "MX1") return true;
            // AC with 2 in the last place: start a tuner cycle (AC002).
            if (c.StartsWith("AC", StringComparison.Ordinal))
                return c.Length > 2 && c[^1] == '2';
            // KY: send from the keyer.
            if (c.StartsWith("KY", StringComparison.Ordinal)) return true;
            // PB: play a voice memory. PB alone only reads it, and PB00 stops it.
            if (c.StartsWith("PB", StringComparison.Ordinal))
                return c[2..].Any(digit => digit != '0');
            // VX1: VOX on, so sound in the room can key the radio.
            if (c == "VX1") return true;
            return false;
        }
    }
}
