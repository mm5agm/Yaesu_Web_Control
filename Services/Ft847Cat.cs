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

        // ── The FT-817, FT-818, FT-857 and FT-897 ──
        //
        // They use the same five-byte blocks, and the same 01 (set frequency),
        // 07 (set mode), 03 (read frequency and mode), E7 (receiver status),
        // F7 (transmit status) and 88 (PTT off) as the FT-847 (FT-817ND,
        // FT-818ND, FT-857D and FT-897 Operating Manuals, "CAT System
        // Programming"). Three differences matter here:
        //   - 00 and 80 are LOCK ON and LOCK OFF on these radios, not CAT ON
        //     and CAT OFF, so nothing is sent when the link opens or closes.
        //   - Their meters are four bits (0-15), not five (Hamlib's ft817.c
        //     and ft857.c; the bit pictures are images in the manuals).
        //   - They have a DIG mode (0A) for data and PKT (0C) for FM packet.
        //     The FT-857 and FT-897 also have FM-N (88); the FT-817 and
        //     FT-818 do not.

        public const byte DigMode = 0x0A;
        public const byte PktMode = 0x0C;

        /// <summary>True for the FT-817, FT-818, FT-857 and FT-897.</summary>
        public static bool IsFt8x7(string model) => model is "FT-817" or "FT-818" or "FT-857" or "FT-897";

        /// <summary>True when 00 / 80 mean CAT ON / OFF and must be sent at open and close: the FT-847 only.</summary>
        public static bool SendsCatOnOff(string model) => !IsFt8x7(model);

        /// <summary>The ID; reply for the model: ID0847, ID0817 and so on.</summary>
        public static string IdReply(string model) =>
            model.Length >= 6 && model.StartsWith("FT-") ? "ID0" + model[3..6] : "ID0847";

        /// <summary>The mode byte for an MD code on <paramref name="model"/>, or null when it has no such mode.</summary>
        public static byte? ModeFromMd(char md, string model)
        {
            if (!IsFt8x7(model)) return ModeFromMd(md);
            var hasFmN = model is "FT-857" or "FT-897";
            return char.ToUpperInvariant(md) switch
            {
                '1' => 0x00,                                // LSB
                '2' => 0x01,                                // USB
                '3' => 0x02,                                // CW
                '7' => 0x03,                                // CW-R
                '5' or 'D' => 0x04,                         // AM (no AM-N)
                '4' => 0x08,                                // FM
                '6' or '8' or '9' or 'C' or 'E' => DigMode, // RTTY, DATA, PSK
                'A' => PktMode,                             // DATA-FM
                'B' or 'F' => hasFmN ? (byte)0x88 : (byte)0x08,
                _ => null
            };
        }

        /// <summary>
        /// The MD code for a mode byte from <paramref name="model"/>. DIG reads
        /// as DATA-U: the reply does not say which side the DIG menu has
        /// chosen, so the transport shows the mode YWC asked for when it can.
        /// </summary>
        public static char? MdFromMode(byte mode, string model)
        {
            if (!IsFt8x7(model)) return MdFromMode(mode);
            return mode switch
            {
                0x00 => '1',
                0x01 => '2',
                0x02 or 0x82 => '3',
                0x03 or 0x83 => '7',
                0x04 or 0x84 => '5',
                0x06 or 0x08 => '4',            // WFM (FT-817/818 receive) shows as FM
                0x88 => 'B',
                DigMode or 0x8A => 'C',
                PktMode or 0x8C => 'A',
                _ => null
            };
        }

        /// <summary>The meter bits of an E7 or F7 reply from <paramref name="model"/>, on YWC's 0-255 scale.</summary>
        public static int MeterTo255(byte status, string model) =>
            IsFt8x7(model) ? (status & 0x0F) * 255 / 15 : MeterTo255(status);

        /// <summary>True when an F7 reply says the radio is transmitting (bit 7 clear).</summary>
        public static bool IsTransmitting(byte txStatus) => (txStatus & 0x80) == 0;

        // ── The one-byte ack ──
        //
        // The FT-817, FT-818, FT-857 and FT-897 answer every block that is
        // not a read with one byte (00), a few milliseconds later. The
        // manuals don't say so; Hamlib's ft817_read_ack reads it after every
        // command for all four. Left unread, it arrives during the NEXT read
        // and pushes that reply one byte along: Felix DF3FX's FT-817 showed
        // 432.039 MHz as 4.320390 MHz (D#379), the 03 reply having become
        // 00 43 20 39 00. So the transport waits for the ack after each set.
        // The FT-847 sends nothing back for a set.

        /// <summary>True when the radio answers each set block with a one-byte ack: the FT-817, FT-818, FT-857 and FT-897.</summary>
        public static bool AcksSets(string model) => IsFt8x7(model);

        // ── Transmit power on the FT-817 and FT-818 ──
        //
        // BD (read TX metering, undocumented) answers two bytes while
        // transmitting (one while receiving): power in the top four bits of
        // the first byte, 0-8 for 0-5 W (Hamlib's ft817.c and its
        // FT817_PWR_CAL, measured by Olivier Schmitt). YWC sends it only
        // when F7 says the radio is transmitting.

        public const byte ReadTxMetering = 0xBD;

        /// <summary>True when YWC reads transmit power with BD: the FT-817 and FT-818.</summary>
        public static bool ReadsPowerWithBd(string model) => model is "FT-817" or "FT-818";

        /// <summary>
        /// The power reading (0-15, 0-8 in use) from a BD reply, on YWC's
        /// 0-255 scale (17 per step). calibration.default.FT-817.json turns
        /// that into watts with Hamlib's table.
        /// </summary>
        public static int PowerFromBd(ReadOnlySpan<byte> reply) => ((reply[0] >> 4) & 0x0F) * 17;

        // ── VFO A and B on the FT-817 and FT-818 ──
        //
        // The manuals' only VFO command is 81, which swaps between A and B;
        // nothing documented says which one the radio is on. The radio keeps
        // it in its EEPROM, and BB (read EEPROM, undocumented) reads it:
        // address 0055, bit 0 of the first reply byte, 0 = A, 1 = B. Hamlib's
        // ft817.c (ft817_get_vfo) has read it this way for years; the address
        // map is the FT8x7EE one (yo3ggx.ro/ft8x7ee). The FT-857 and FT-897
        // keep it elsewhere (0068), and some FT-857s cannot be read at all,
        // so they are left as they were until someone can try one.
        //
        // BC is the matching EEPROM WRITE. A wrong write can wipe the radio's
        // calibration, so YWC never sends it; the tests prove it.

        public const byte ToggleVfo = 0x81;
        public const byte ReadEeprom = 0xBB;
        public const byte WriteEeprom = 0xBC;   // never sent
        public const ushort VfoEepromAddress = 0x0055;

        /// <summary>True when YWC reads which VFO the radio is on (A or B) from its EEPROM: the FT-817 and FT-818.</summary>
        public static bool ReadsVfoFromEeprom(string model) => model is "FT-817" or "FT-818";

        /// <summary>A BB block reading two EEPROM bytes from <paramref name="address"/>.</summary>
        public static byte[] ReadEepromBlock(ushort address) =>
            new byte[] { (byte)(address >> 8), (byte)(address & 0xFF), 0, 0, ReadEeprom };

        /// <summary>0 for VFO A, 1 for VFO B, from the reply to a BB read of address 0055.</summary>
        public static int VfoFromEeprom(ReadOnlySpan<byte> reply) => reply[0] & 0x01;
    }
}
