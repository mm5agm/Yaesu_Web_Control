using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Yaesu_Web_Control.Services.Audio
{
    /// <summary>
    /// Length-prefixed binary framing for the /audio WebSocket.
    /// Layout: length(u32 BE) | type(u8) | seq(u32 BE) | payload
    /// </summary>
    public static class AudioWireProtocol
    {
        public const int HeaderSize = 1 + 4; // type + seq (length is outside)

        public static byte[] Frame(byte type, uint seq, ReadOnlySpan<byte> payload)
        {
            int bodyLen = HeaderSize + payload.Length;
            var buf = new byte[4 + bodyLen];
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), (uint)bodyLen);
            buf[4] = type;
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(5, 4), seq);
            payload.CopyTo(buf.AsSpan(9));
            return buf;
        }

        public static byte[] FrameControl(uint seq, object payload)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(payload);
            return Frame(AudioConstants.MsgControl, seq, json);
        }

        public static bool TryParse(ReadOnlySpan<byte> body, out byte type, out uint seq, out ReadOnlySpan<byte> payload)
        {
            type = 0;
            seq = 0;
            payload = default;
            if (body.Length < HeaderSize) return false;
            type = body[0];
            seq = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(1, 4));
            payload = body.Slice(HeaderSize);
            return true;
        }

        public static string ControlJson(ReadOnlySpan<byte> payload) =>
            Encoding.UTF8.GetString(payload);
    }
}
