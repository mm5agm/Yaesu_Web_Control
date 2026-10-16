using System;
using System.Collections.Generic;
using System.Text;

namespace RadioWebControl.Core.Services.Rtty
{
    /// <summary>
    /// Which figures table to use above the shift.
    ///
    /// The letters are the same the world over; the figures are not. ITA-2
    /// leaves four positions to national use, and the American teleprinters
    /// that amateur RTTY grew out of filled them in differently. A European
    /// weather station and a ham on 20 m can send the identical five bits and
    /// mean different characters, so this is the receiving operator's choice,
    /// not something that can be read off the air.
    /// </summary>
    public enum RttyFigureSet
    {
        /// <summary>The international table. Right for commercial and utility stations.</summary>
        Ita2,

        /// <summary>The American variant, which is what amateur RTTY uses in practice.</summary>
        UsTty,
    }

    /// <summary>
    /// Baudot / ITA-2: five bits per character, and a shift state that says
    /// whether those five bits mean a letter or a figure.
    ///
    /// <para><b>Both directions live here on purpose.</b> Receiving needs the
    /// table one way round and transmitting needs it the other, and the two
    /// must agree exactly or a message goes out as mojibake. Writing them as
    /// one unit, with a round-trip test over the whole printable set, is what
    /// makes that agreement checkable rather than hoped for. Only the decode
    /// half is wired up today; the encode half is here so that adding a
    /// transmitter is a question of keying bits, not of re-deriving the
    /// alphabet.</para>
    ///
    /// <para>Codes are held as integers 0-31 where <b>bit 0 is the bit sent
    /// first</b>. That is the usual convention in software and it means the
    /// framing layer can shift bits out of the bottom in transmission order
    /// without reversing anything.</para>
    ///
    /// <para><b>USOS</b> - unshift on space - is the one piece of folklore that
    /// has to be here rather than above. A figures shift lost to a noise burst
    /// turns the rest of a transmission into gibberish, so by long convention a
    /// space returns the receiver to letters. It is near-universal but not
    /// quite universal, which is why the radio has a switch for it in both
    /// directions (SET &gt; Function &gt; RTTY DECODE USOS, and TX USOS) and why
    /// this class does too.</para>
    /// </summary>
    public static class Ita2Codec
    {
        /// <summary>Shift to letters. Also the idle character - an idling line sends these forever.</summary>
        public const byte Ltrs = 0b11111;   // 31

        /// <summary>Shift to figures.</summary>
        public const byte Figs = 0b11011;   // 27

        /// <summary>Space. The same code in both shifts, and what USOS keys off.</summary>
        public const byte Space = 0b00100;  // 4

        /// <summary>Carriage return.</summary>
        public const byte Cr = 0b01000;     // 8

        /// <summary>Line feed.</summary>
        public const byte Lf = 0b00010;     // 2

        /// <summary>Blank. Prints nothing; what an unmodulated line decodes to.</summary>
        public const byte Blank = 0b00000;  // 0

        // Index is the five-bit code, bit 0 sent first. '\0' marks a position
        // that prints nothing - the two shifts, blank, and the national
        // positions that one table or the other leaves empty.
        private static readonly char[] Letters = BuildLetters();
        private static readonly char[] FiguresIta2 = BuildFigures(RttyFigureSet.Ita2);
        private static readonly char[] FiguresUs = BuildFigures(RttyFigureSet.UsTty);

        private static char[] BuildLetters()
        {
            var t = new char[32];
            // Written as the sent bit pattern, lowest bit first, so the table
            // can be checked against any published listing by eye.
            t[0b00011] = 'A'; t[0b11001] = 'B'; t[0b01110] = 'C';
            t[0b01001] = 'D'; t[0b00001] = 'E'; t[0b01101] = 'F';
            t[0b11010] = 'G'; t[0b10100] = 'H'; t[0b00110] = 'I';
            t[0b01011] = 'J'; t[0b01111] = 'K'; t[0b10010] = 'L';
            t[0b11100] = 'M'; t[0b01100] = 'N'; t[0b11000] = 'O';
            t[0b10110] = 'P'; t[0b10111] = 'Q'; t[0b01010] = 'R';
            t[0b00101] = 'S'; t[0b10000] = 'T'; t[0b00111] = 'U';
            t[0b11110] = 'V'; t[0b10011] = 'W'; t[0b11101] = 'X';
            t[0b10101] = 'Y'; t[0b10001] = 'Z';
            t[Space] = ' '; t[Cr] = '\r'; t[Lf] = '\n';
            return t;
        }

        private static char[] BuildFigures(RttyFigureSet set)
        {
            var t = new char[32];

            // Shared by both tables.
            t[0b00001] = '3'; t[0b00011] = '-'; t[0b00110] = '8';
            t[0b00111] = '7'; t[0b01010] = '4'; t[0b01011] = '\a';
            t[0b01100] = ','; t[0b01110] = ':'; t[0b01111] = '(';
            t[0b10000] = '5'; t[0b10001] = '+'; t[0b10010] = ')';
            t[0b10011] = '2'; t[0b10101] = '6'; t[0b10110] = '0';
            t[0b10111] = '1'; t[0b11000] = '9'; t[0b11001] = '?';
            t[0b11100] = '.'; t[0b11101] = '/';
            t[Space] = ' '; t[Cr] = '\r'; t[Lf] = '\n';

            // The positions the two variants disagree about. ITA-2 reserves D
            // for WRU ("who are you?") and gives S an apostrophe; the American
            // machines put currency and a bell there, and amateur RTTY
            // inherited those. F, G and H are national-use in ITA-2 and print
            // nothing here rather than guessing at a nation.
            if (set == RttyFigureSet.UsTty)
            {
                t[0b01001] = '$';   // D
                t[0b01101] = '!';   // F
                t[0b11010] = '&';   // G
                t[0b10100] = '#';   // H
                t[0b00101] = '\a';  // S - bell
                t[0b11110] = ';';   // V
            }
            else
            {
                t[0b00101] = '\'';  // S
                t[0b11110] = '=';   // V
            }

            return t;
        }

        private static char[] Figures(RttyFigureSet set) =>
            set == RttyFigureSet.UsTty ? FiguresUs : FiguresIta2;

        /// <summary>
        /// The character a code prints in the given shift, or '\0' for one that
        /// prints nothing (the two shift codes, blank, and the national
        /// positions this table leaves empty).
        /// </summary>
        public static char ToChar(byte code, bool figures, RttyFigureSet set = RttyFigureSet.Ita2)
        {
            if (code > 31) throw new ArgumentOutOfRangeException(nameof(code));
            if (code == Ltrs || code == Figs) return '\0';
            return figures ? Figures(set)[code] : Letters[code];
        }

        /// <summary>
        /// The code for a character, and whether it needs the figures shift.
        /// Case is folded - Baudot has no lower case. False for anything the
        /// table cannot carry.
        /// </summary>
        public static bool TryGetCode(char c, RttyFigureSet set, out byte code, out bool figures)
        {
            c = char.ToUpperInvariant(c);

            for (int i = 0; i < 32; i++)
            {
                if (Letters[i] == c) { code = (byte)i; figures = false; return true; }
            }

            var figs = Figures(set);
            for (int i = 0; i < 32; i++)
            {
                if (figs[i] == c) { code = (byte)i; figures = true; return true; }
            }

            code = 0;
            figures = false;
            return false;
        }

        /// <summary>
        /// Text to a stream of five-bit codes, shifts included - the transmit
        /// half. Unknown characters are dropped rather than sent as something
        /// else, because a wrong character on the air is worse than a missing
        /// one.
        /// </summary>
        /// <param name="text">What to send. Case is folded.</param>
        /// <param name="set">Which figures table the far end is using.</param>
        /// <param name="usos">
        /// Re-send the figures shift after a space. Must match what the
        /// receiving station does, which is why the radio has its own TX USOS
        /// switch; on means the message survives a receiver that unshifts.
        /// </param>
        /// <param name="startInLetters">
        /// Emit a leading LTRS so the far end is in a known shift. Normally
        /// wanted: the receiver's shift state is whatever the last transmission
        /// left it in.
        /// </param>
        public static IReadOnlyList<byte> Encode(
            string text,
            RttyFigureSet set = RttyFigureSet.Ita2,
            bool usos = true,
            bool startInLetters = true)
        {
            var codes = new List<byte>((text?.Length ?? 0) + 8);
            if (string.IsNullOrEmpty(text)) return codes;

            bool inFigures = false;
            if (startInLetters) codes.Add(Ltrs);

            foreach (var raw in text!)
            {
                // A newline goes out as the pair a teleprinter needs: carriage
                // return to bring the head back, then line feed to roll the
                // platen. Sent the other way round, the first characters of the
                // new line are printed while the head is still travelling.
                if (raw == '\n') { codes.Add(Cr); codes.Add(Lf); continue; }
                if (raw == '\r') continue;   // folded into the '\n' above

                if (!TryGetCode(raw, set, out var code, out var needFigures)) continue;

                // Space, CR and LF sit at the same code in both shifts, so they
                // never force one.
                bool shiftFree = code == Space || code == Cr || code == Lf;

                if (!shiftFree && needFigures != inFigures)
                {
                    codes.Add(needFigures ? Figs : Ltrs);
                    inFigures = needFigures;
                }

                codes.Add(code);

                // The far end drops back to letters on a space, so the next
                // figure has to say so again.
                if (usos && code == Space && inFigures) inFigures = false;
            }

            return codes;
        }
    }

    /// <summary>
    /// The receiving half's shift state, fed one five-bit code at a time.
    ///
    /// Separate from the table because it is a state machine and the table is
    /// not: it has to be held across characters, reset between overs, and asked
    /// what it is currently doing.
    /// </summary>
    public sealed class Ita2Decoder
    {
        private readonly RttyFigureSet _set;
        private readonly bool _usos;

        public Ita2Decoder(RttyFigureSet set = RttyFigureSet.Ita2, bool usos = true)
        {
            _set = set;
            _usos = usos;
        }

        /// <summary>True when the next code will be read from the figures table.</summary>
        public bool InFigures { get; private set; }

        /// <summary>Back to letters, as at the start of an over.</summary>
        public void Reset() => InFigures = false;

        /// <summary>
        /// One code in, the text it prints out - usually one character, empty
        /// for a shift or a blank.
        /// </summary>
        public string Feed(byte code)
        {
            if (code == Ita2Codec.Figs) { InFigures = true; return string.Empty; }
            if (code == Ita2Codec.Ltrs) { InFigures = false; return string.Empty; }

            var c = Ita2Codec.ToChar(code, InFigures, _set);

            if (code == Ita2Codec.Space && _usos) InFigures = false;

            return c == '\0' ? string.Empty : c.ToString();
        }

        /// <summary>Feed a run of codes and get everything they print.</summary>
        public string Feed(IEnumerable<byte> codes)
        {
            var sb = new StringBuilder();
            foreach (var c in codes) sb.Append(Feed(c));
            return sb.ToString();
        }
    }
}
