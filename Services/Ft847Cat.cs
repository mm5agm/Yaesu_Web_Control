namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// The FT-847's CAT protocol: five-byte blocks, four argument bytes then the
    /// opcode, 8 data bits, no parity, two stop bits (FT-847 Operating Manual,
    /// "CAT System Programming", pp. 91-93). It is the older Yaesu binary CAT,
    /// nothing like the text commands (FA014074000;) of every other radio YWC
    /// supports, so <see cref="Ft847CatTransport"/> translates between the two.
    ///
    /// The bit layouts of the three read replies (Notes 1-3 in the manual) are
    /// pictures in the PDF, so they are taken from Hamlib's ft847.c, which has
    /// driven the radio for twenty years:
    ///   E7 (receiver status): one byte, bit 7 set = squelch closed,
    ///      bits 0-4 = S-meter 0-31.
    ///   F7 (transmit status): one byte, bit 7 CLEAR = transmitting,
    ///      bits 0-4 = PO meter 0-31.
    ///   03 (main VFO frequency and mode): five bytes, four BCD frequency bytes
    ///      in 10 Hz units, most significant first, then the mode byte.
    /// </summary>
    public static class Ft847Cat
    {
        public const byte CatOn = 0x00;
        public const byte CatOff = 0x80;
        public const byte PttOff = 0x88;
        public const byte SetMainFrequency = 0x01;
        public const byte SetMainMode = 0x07;
        public const byte ReadRxStatus = 0xE7;
        public const byte ReadTxStatus = 0xF7;
        public const byte ReadMainFrequencyAndMode = 0x03;

        public const long MinHz = 100_000;
        public const long MaxHz = 512_000_000;

        /// <summary>A block with dummy (zero) arguments.</summary>
        public static byte[] Block(byte opcode) => new byte[] { 0, 0, 0, 0, opcode };

        /// <summary>
        /// The four BCD bytes for a frequency, in 10 Hz units, most significant
        /// first: 432.1 MHz is 43 21 00 00, 14.074 MHz is 01 40 74 00. The radio
        /// cannot be set closer than 10 Hz over CAT, so the frequency is rounded.
        /// </summary>
        public static byte[] FrequencyBcd(long hz)
        {
            if (hz < 0 || hz > 999_999_990) throw new ArgumentOutOfRangeException(nameof(hz));
            var tens = (hz + 5) / 10;
            var digits = tens.ToString("D8");
            var bytes = new byte[4];
            for (int i = 0; i < 4; i++)
                bytes[i] = (byte)(((digits[2 * i] - '0') << 4) | (digits[2 * i + 1] - '0'));
            return bytes;
        }

        /// <summary>The frequency in Hz from four BCD bytes, or null if a nibble is not a digit.</summary>
        public static long? FrequencyFromBcd(ReadOnlySpan<byte> bcd)
        {
            if (bcd.Length < 4) return null;
            long tens = 0;
            for (int i = 0; i < 4; i++)
            {
                int hi = bcd[i] >> 4, lo = bcd[i] & 0x0F;
                if (hi > 9 || lo > 9) return null;
                tens = tens * 100 + hi * 10 + lo;
            }
            return tens * 10;
        }

        public static byte[] SetFrequencyBlock(long hz)
        {
            var b = FrequencyBcd(hz);
            return new byte[] { b[0], b[1], b[2], b[3], SetMainFrequency };
        }

        public static byte[] SetModeBlock(byte ft847Mode) => new byte[] { ft847Mode, 0, 0, 0, SetMainMode };

        /// <summary>
        /// The FT-847 mode byte for a modern Yaesu MD code, or null when there is
        /// no such mode on the radio. The FT-847 has no DATA or RTTY modes: data
        /// is sent as SSB audio, so DATA-U, RTTY-U and PSK go out as USB, DATA-L
        /// and RTTY-L as LSB, and DATA-FM as FM.
        /// </summary>
        public static byte? ModeFromMd(char md) => char.ToUpperInvariant(md) switch
        {
            '1' or '6' or '8' => 0x00,          // LSB, RTTY-L, DATA-L
            '2' or '9' or 'C' or 'E' => 0x01,   // USB, RTTY-U, DATA-U, PSK
            '3' => 0x02,                        // CW
            '7' => 0x03,                        // CW-R
            '5' => 0x04,                        // AM
            '4' or 'A' => 0x08,                 // FM, DATA-FM
            'D' => 0x84,                        // AM-N
            'B' or 'F' => 0x88,                 // FM-N, DATA-FM-N
            _ => null
        };

        /// <summary>
        /// The modern MD code for an FT-847 mode byte, or null for a byte the
        /// manual does not list. CW(N) and CW-R(N) read as plain CW and CW-R:
        /// YWC has no narrow CW mode, the narrow filter is the radio's own.
        /// </summary>
        public static char? MdFromMode(byte mode) => mode switch
        {
            0x00 => '1',
            0x01 => '2',
            0x02 or 0x82 => '3',
            0x03 or 0x83 => '7',
            0x04 => '5',
            0x08 => '4',
            0x84 => 'D',
            0x88 => 'B',
            _ => null
        };

        /// <summary>The meter bits (0-31) of an E7 or F7 reply, on YWC's 0-255 scale.</summary>
        public static int MeterTo255(byte status) => (status & 0x1F) * 255 / 31;

        /// <summary>True when an F7 reply says the radio is transmitting (bit 7 clear).</summary>
        public static bool IsTransmitting(byte txStatus) => (txStatus & 0x80) == 0;
    }
}
