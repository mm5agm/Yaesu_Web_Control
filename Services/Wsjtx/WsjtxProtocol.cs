using System.Buffers.Binary;
using System.Text;

namespace Yaesu_Web_Control.Services.Wsjtx
{
    /// <summary>
    /// One Decode (type 2) message from WSJT-X or WSJT-Z, with every field kept
    /// exactly as received. A Reply has to echo these fields unchanged: WSJT-X
    /// finds the decode to answer by matching time, SNR, DT, DF, mode and
    /// message text, and ignores the Reply if any of them differ.
    /// </summary>
    public sealed record WsjtxDecode(
        string Id,
        bool IsNew,
        uint TimeMs,
        int Snr,
        double DeltaTime,
        uint DeltaFrequency,
        string Mode,
        string Message,
        bool LowConfidence,
        bool OffAir);

    /// <summary>
    /// The Status (type 1) fields the big-tile page uses. Later fields are read
    /// if present; an older or shorter Status leaves them empty.
    /// </summary>
    public sealed record WsjtxStatus(
        string Id,
        long DialFrequency,
        string Mode,
        string DxCall,
        bool TxEnabled,
        bool Transmitting,
        bool Decoding,
        string DeCall,
        string TxMessage = "",
        string DeGrid = "");

    /// <summary>
    /// The QSO Logged (type 5) fields the big-tile page shows. WSJT-X sends it
    /// once a QSO is in its log: after the operator clicks OK in the Log QSO
    /// window, or straight away when "Prompt me to log QSO" is unticked.
    /// </summary>
    public sealed record WsjtxQsoLogged(
        string Id,
        string DxCall,
        string DxGrid,
        long TxFrequencyHz,
        string Mode,
        string ReportSent,
        string ReportReceived);

    /// <summary>
    /// Reads and writes the WSJT-X UDP message format (NetworkMessage.hpp):
    /// Qt QDataStream, big-endian, a magic number, a schema number, a message
    /// type and the sender's Id, then fields that depend on the type.
    /// Radio-agnostic: nothing here knows about Yaesu.
    /// </summary>
    public static class WsjtxProtocol
    {
        public const uint MagicNumber = 0xADBCCBDA;

        // Schema 3 is the newest. Configure's Auto CQ and Auto Call fields are
        // only read when the header says 3 or more, so outgoing messages use 3.
        public const uint OutgoingSchema = 3;

        public const uint TypeHeartbeat = 0;
        public const uint TypeStatus = 1;
        public const uint TypeDecode = 2;
        public const uint TypeClear = 3;
        public const uint TypeReply = 4;
        public const uint TypeQsoLogged = 5;
        public const uint TypeClose = 6;
        public const uint TypeHaltTx = 8;
        public const uint TypeConfigure = 15;

        // "No change" values for Configure's numeric fields (quint32 max).
        private const uint NoChange = uint.MaxValue;

        public readonly record struct Header(uint Schema, uint Type, string Id, int BodyOffset);

        public static bool TryReadHeader(ReadOnlySpan<byte> data, out Header header)
        {
            header = default;
            try
            {
                int offset = 0;
                if (data.Length < 12 || ReadUInt32(data, ref offset) != MagicNumber) return false;
                uint schema = ReadUInt32(data, ref offset);
                uint type = ReadUInt32(data, ref offset);
                string id = ReadUtf8(data, ref offset) ?? "";
                header = new Header(schema, type, id, offset);
                return true;
            }
            catch (ArgumentOutOfRangeException) { return false; }
            catch (IndexOutOfRangeException) { return false; }
        }

        public static WsjtxDecode? TryParseDecode(byte[] data)
        {
            if (!TryReadHeader(data, out var h) || h.Type != TypeDecode) return null;
            try
            {
                var span = data.AsSpan();
                int offset = h.BodyOffset;
                bool isNew = ReadBool(span, ref offset);
                uint time = ReadUInt32(span, ref offset);
                int snr = (int)ReadUInt32(span, ref offset);
                double dt = BinaryPrimitives.ReadDoubleBigEndian(span.Slice(offset, 8)); offset += 8;
                uint df = ReadUInt32(span, ref offset);
                string mode = ReadUtf8(span, ref offset) ?? "";
                string message = ReadUtf8(span, ref offset) ?? "";
                // Low confidence and Off air were added later; older senders stop here.
                bool lowConf = offset < span.Length && ReadBool(span, ref offset);
                bool offAir = offset < span.Length && ReadBool(span, ref offset);
                return new WsjtxDecode(h.Id, isNew, time, snr, dt, df, mode, message, lowConf, offAir);
            }
            catch (ArgumentOutOfRangeException) { return null; }
            catch (IndexOutOfRangeException) { return null; }
        }

        public static WsjtxStatus? TryParseStatus(byte[] data)
        {
            if (!TryReadHeader(data, out var h) || h.Type != TypeStatus) return null;
            var span = data.AsSpan();
            int offset = h.BodyOffset;
            long dial;
            string mode, dxCall;
            bool txEnabled, transmitting;
            try
            {
                dial = (long)ReadUInt64(span, ref offset);
                mode = ReadUtf8(span, ref offset) ?? "";
                dxCall = ReadUtf8(span, ref offset) ?? "";
                ReadUtf8(span, ref offset);              // Report
                ReadUtf8(span, ref offset);              // Tx Mode
                txEnabled = ReadBool(span, ref offset);
                transmitting = ReadBool(span, ref offset);
            }
            catch (ArgumentOutOfRangeException) { return null; }
            catch (IndexOutOfRangeException) { return null; }

            // The rest is optional: a short Status still gives the fields above.
            bool decoding = false;
            string deCall = "";
            string deGrid = "";
            string txMessage = "";
            try
            {
                decoding = ReadBool(span, ref offset);
                ReadUInt32(span, ref offset);            // Rx DF
                ReadUInt32(span, ref offset);            // Tx DF
                deCall = ReadUtf8(span, ref offset) ?? "";
                deGrid = (ReadUtf8(span, ref offset) ?? "").Trim();   // DE grid: the operator's own grid (Spot Map)
                ReadUtf8(span, ref offset);              // DX grid
                ReadBool(span, ref offset);              // Tx Watchdog
                ReadUtf8(span, ref offset);              // Sub-mode
                ReadBool(span, ref offset);              // Fast mode
                offset += 1;                             // Special Operation Mode (quint8)
                ReadUInt32(span, ref offset);            // Frequency Tolerance
                ReadUInt32(span, ref offset);            // T/R Period
                ReadUtf8(span, ref offset);              // Configuration Name
                txMessage = (ReadUtf8(span, ref offset) ?? "").Trim();   // Tx Message: what WSJT-X sends, or last sent
            }
            catch (ArgumentOutOfRangeException) { }
            catch (IndexOutOfRangeException) { }

            return new WsjtxStatus(h.Id, dial, mode, dxCall, txEnabled, transmitting, decoding, deCall, txMessage, deGrid);
        }

        public static WsjtxQsoLogged? TryParseQsoLogged(byte[] data)
        {
            if (!TryReadHeader(data, out var h) || h.Type != TypeQsoLogged) return null;
            try
            {
                var span = data.AsSpan();
                int offset = h.BodyOffset;
                if (!SkipDateTime(span, ref offset)) return null;   // Date & Time Off
                string call = ReadUtf8(span, ref offset) ?? "";
                string grid = ReadUtf8(span, ref offset) ?? "";
                long freq = (long)ReadUInt64(span, ref offset);
                string mode = ReadUtf8(span, ref offset) ?? "";
                string sent = ReadUtf8(span, ref offset) ?? "";
                string rcvd = ReadUtf8(span, ref offset) ?? "";
                if (call.Length == 0) return null;
                return new WsjtxQsoLogged(h.Id, call, grid, freq, mode, sent, rcvd);
            }
            catch (ArgumentOutOfRangeException) { return null; }
            catch (IndexOutOfRangeException) { return null; }
        }

        // QDateTime: qint64 Julian day, quint32 ms since midnight, quint8 time
        // spec, then a qint32 offset if the spec is OffsetFromUTC (2). WSJT-X
        // sends UTC (1). A time-zone spec (3) carries a zone we don't read.
        private static bool SkipDateTime(ReadOnlySpan<byte> data, ref int offset)
        {
            offset += 8 + 4;
            byte spec = data[offset++];
            if (spec == 2) offset += 4;
            return spec <= 2;
        }

        /// <summary>
        /// Reply (type 4): asks WSJT-X to answer a decode, as if the operator had
        /// double-clicked it. Stock WSJT-X only acts on CQ and QRZ decodes;
        /// WSJT-Z acts on any decode. Needs "Accept UDP requests" ticked.
        /// </summary>
        public static byte[] BuildReply(WsjtxDecode d, string targetId, byte modifiers = 0)
        {
            var w = new Writer(TypeReply, targetId);
            w.UInt32(d.TimeMs);
            w.UInt32((uint)d.Snr);
            w.Double(d.DeltaTime);
            w.UInt32(d.DeltaFrequency);
            w.Utf8(d.Mode);
            w.Utf8(d.Message);
            w.Bool(d.LowConfidence);
            w.Byte(modifiers);
            return w.ToArray();
        }

        /// <summary>HaltTx (type 8). autoTxOnly=false stops transmitting now.</summary>
        public static byte[] BuildHaltTx(string targetId, bool autoTxOnly)
        {
            var w = new Writer(TypeHaltTx, targetId);
            w.Bool(autoTxOnly);
            return w.ToArray();
        }

        /// <summary>
        /// Configure (type 15), WSJT-Z's schema-3 form, changing only Auto CQ and
        /// Auto Call. Every other field carries its "no change" value. WSJT-Z
        /// sets both check boxes from every Configure it accepts, so the caller
        /// always says what Auto CQ should be too.
        /// </summary>
        public static byte[] BuildAutoConfigure(string targetId, bool autoCq, bool autoCall)
        {
            var w = new Writer(TypeConfigure, targetId);
            w.Utf8("");          // Mode: no change
            w.UInt32(NoChange);  // Frequency tolerance
            w.Utf8("");          // Sub-mode
            w.Bool(false);       // Fast mode
            w.UInt32(NoChange);  // T/R period
            w.UInt32(NoChange);  // Rx DF
            w.Utf8("");          // DX call
            w.Utf8("");          // DX grid
            w.Bool(false);       // Generate messages
            w.Bool(autoCq);      // WSJT-Z, schema 3+
            w.Bool(autoCall);    // WSJT-Z, schema 3+
            return w.ToArray();
        }

        // --- Reading ---

        private static uint ReadUInt32(ReadOnlySpan<byte> data, ref int offset)
        {
            var value = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            offset += 4;
            return value;
        }

        private static ulong ReadUInt64(ReadOnlySpan<byte> data, ref int offset)
        {
            var value = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset, 8));
            offset += 8;
            return value;
        }

        private static bool ReadBool(ReadOnlySpan<byte> data, ref int offset) => data[offset++] != 0;

        // utf8 = QByteArray: quint32 length, then the bytes; 0xFFFFFFFF is null.
        private static string? ReadUtf8(ReadOnlySpan<byte> data, ref int offset)
        {
            uint raw = ReadUInt32(data, ref offset);
            if (raw == uint.MaxValue) return null;
            int length = checked((int)raw);
            var str = Encoding.UTF8.GetString(data.Slice(offset, length));
            offset += length;
            return str;
        }

        // --- Writing ---

        private sealed class Writer
        {
            private readonly MemoryStream _ms = new();

            public Writer(uint type, string id)
            {
                UInt32(MagicNumber);
                UInt32(OutgoingSchema);
                UInt32(type);
                Utf8(id);
            }

            public void UInt32(uint v)
            {
                Span<byte> b = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(b, v);
                _ms.Write(b);
            }

            public void Double(double v)
            {
                Span<byte> b = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(b, v);
                _ms.Write(b);
            }

            public void Bool(bool v) => _ms.WriteByte(v ? (byte)1 : (byte)0);
            public void Byte(byte v) => _ms.WriteByte(v);

            public void Utf8(string? s)
            {
                if (s is null) { UInt32(uint.MaxValue); return; }
                var bytes = Encoding.UTF8.GetBytes(s);
                UInt32((uint)bytes.Length);
                _ms.Write(bytes);
            }

            public byte[] ToArray() => _ms.ToArray();
        }

        // Test helper: builds a Decode or Status as WSJT-X would send it.
        internal static byte[] BuildDecodeForTest(WsjtxDecode d, uint schema = 2)
        {
            var w = new Writer(TypeDecode, d.Id);
            w.Bool(d.IsNew);
            w.UInt32(d.TimeMs);
            w.UInt32((uint)d.Snr);
            w.Double(d.DeltaTime);
            w.UInt32(d.DeltaFrequency);
            w.Utf8(d.Mode);
            w.Utf8(d.Message);
            w.Bool(d.LowConfidence);
            w.Bool(d.OffAir);
            var bytes = w.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), schema);
            return bytes;
        }

        internal static byte[] BuildStatusForTest(WsjtxStatus s)
        {
            var w = new Writer(TypeStatus, s.Id);
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)s.DialFrequency);
            foreach (var x in b) w.Byte(x);
            w.Utf8(s.Mode);
            w.Utf8(s.DxCall);
            w.Utf8("");          // Report
            w.Utf8(s.Mode);      // Tx Mode
            w.Bool(s.TxEnabled);
            w.Bool(s.Transmitting);
            w.Bool(s.Decoding);
            w.UInt32(1500);      // Rx DF
            w.UInt32(1500);      // Tx DF
            w.Utf8(s.DeCall);
            w.Utf8("IO85");      // DE grid
            return w.ToArray();
        }
    }
}
