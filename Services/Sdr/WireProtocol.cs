// Wire protocol between YWC main and an SDR worker.
//
// All multi-byte integers are big-endian. Floats are IEEE 754 single-precision
// in network byte order.
//
//   Framing
//   ───────
//   Every message starts with:
//     [4 bytes]  payloadLength (uint32, BE) — number of bytes in the payload
//                                              that follows, NOT including
//                                              the type byte
//     [1 byte]   messageType  (see MessageType enum)
//     [payloadLength bytes]  payload (varies by type)
//
//   Total bytes on wire = 4 + 1 + payloadLength.
//
//   The 4-byte length lets the reader pre-allocate, and lets us add new
//   message types without breaking the framing.
//
//   Messages: worker → main
//   ───────────────────────
//   SpectrumFrame (type 0x01)
//     [8 bytes]   sequence    (uint64) — frame counter, monotonically increasing
//     [8 bytes]   centreHz    (int64)  — centre frequency in Hz
//     [8 bytes]   spanHz      (int64)  — full visible span in Hz (the achieved IQ rate)
//     [4 bytes]   binCount    (int32)  — number of float bins that follow
//     [binCount × 4 bytes]  bins (float32 each, BE) — dBFS values
//
//   StatusUpdate (type 0x02)
//     [UTF-8 string]  status text  — e.g. "connecting", "streaming", "nodll"
//
//   ErrorReport (type 0x03)
//     [UTF-8 string]  error message — human-readable diagnostic
//
//   Messages: main → worker
//   ───────────────────────
//   DspSettings (type 0x04)
//     [4 bytes]  gainLinear  (float32, BE) — pre-dB gain G (§4.1)
//     [4 bytes]  dbFloor     (float32, BE) — display clamp lower bound
//     [4 bytes]  dbCeiling   (float32, BE) — display clamp upper bound
//
//   Sample-rate / FFT-size changes still go via worker respawn; only the
//   live spectrum-rendering knobs travel through this control channel so
//   slider drag is smooth.

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Yaesu_Web_Control.Services.Sdr;

public enum MessageType : byte
{
    SpectrumFrame = 0x01,
    StatusUpdate  = 0x02,
    ErrorReport   = 0x03,
    DspSettings   = 0x04,
}

/// <summary>
/// Payload of a DspSettings message. Used by both ends of the protocol —
/// FrameWriter on main writes it, ControlReader on the worker reads it.
/// </summary>
public readonly record struct DspSettingsPayload(float GainLinear, float DbFloor, float DbCeiling);

/// <summary>
/// Frame writer for the worker side. Encapsulates the length-prefix framing
/// and big-endian field layout so worker code can just call high-level
/// methods like <see cref="WriteSpectrumAsync"/>.
/// </summary>
public sealed class FrameWriter
{
    private readonly NetworkStream _stream;
    // Reusable buffer for the frame header + small payloads. Spectrum frames
    // allocate a one-shot buffer per call (bin count varies).
    private readonly byte[] _headerBuf = new byte[5];   // 4-byte length + 1-byte type

    public FrameWriter(NetworkStream stream) => _stream = stream;

    public async Task WriteSpectrumAsync(
        ulong sequence, long centreHz, long spanHz, float[] bins, CancellationToken ct)
    {
        // Payload layout: 8 + 8 + 8 + 4 + binCount*4
        int payloadLen = 8 + 8 + 8 + 4 + bins.Length * 4;
        var buf = new byte[5 + payloadLen];   // length + type + payload
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), (uint)payloadLen);
        buf[4] = (byte)MessageType.SpectrumFrame;
        int o = 5;
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(o, 8), sequence); o += 8;
        BinaryPrimitives.WriteInt64BigEndian (buf.AsSpan(o, 8), centreHz); o += 8;
        BinaryPrimitives.WriteInt64BigEndian (buf.AsSpan(o, 8), spanHz);   o += 8;
        BinaryPrimitives.WriteInt32BigEndian (buf.AsSpan(o, 4), bins.Length); o += 4;
        for (int i = 0; i < bins.Length; i++)
        {
            BinaryPrimitives.WriteSingleBigEndian(buf.AsSpan(o, 4), bins[i]);
            o += 4;
        }
        await _stream.WriteAsync(buf.AsMemory(), ct).ConfigureAwait(false);
    }

    public Task WriteStatusAsync(string status, CancellationToken ct) =>
        WriteStringMessageAsync(MessageType.StatusUpdate, status, ct);

    public Task WriteErrorAsync(string error, CancellationToken ct) =>
        WriteStringMessageAsync(MessageType.ErrorReport, error, ct);

    /// <summary>Main → worker: live DSP knob update. Three floats, 12-byte payload.</summary>
    public async Task WriteDspSettingsAsync(DspSettingsPayload settings, CancellationToken ct)
    {
        const int payloadLen = 12;
        var buf = new byte[5 + payloadLen];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), payloadLen);
        buf[4] = (byte)MessageType.DspSettings;
        BinaryPrimitives.WriteSingleBigEndian(buf.AsSpan(5,  4), settings.GainLinear);
        BinaryPrimitives.WriteSingleBigEndian(buf.AsSpan(9,  4), settings.DbFloor);
        BinaryPrimitives.WriteSingleBigEndian(buf.AsSpan(13, 4), settings.DbCeiling);
        await _stream.WriteAsync(buf.AsMemory(), ct).ConfigureAwait(false);
    }

    private async Task WriteStringMessageAsync(MessageType type, string text, CancellationToken ct)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        BinaryPrimitives.WriteUInt32BigEndian(_headerBuf.AsSpan(0, 4), (uint)payload.Length);
        _headerBuf[4] = (byte)type;
        await _stream.WriteAsync(_headerBuf.AsMemory(0, 5), ct).ConfigureAwait(false);
        await _stream.WriteAsync(payload.AsMemory(), ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Worker-side reader for main → worker control messages. Keeps reading
/// frames on a background task; raises <see cref="DspSettingsReceived"/>
/// each time a DspSettings message arrives. Unknown message types are
/// skipped (their length prefix lets us advance past them safely so we
/// stay forwards-compatible with future control messages).
/// </summary>
public sealed class ControlReader
{
    private readonly NetworkStream _stream;
    private readonly byte[]        _hdr = new byte[5];

    public ControlReader(NetworkStream stream) => _stream = stream;

    public event Action<DspSettingsPayload>? DspSettingsReceived;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExactAsync(_hdr, ct).ConfigureAwait(false)) return;
            int payloadLen = (int)BinaryPrimitives.ReadUInt32BigEndian(_hdr.AsSpan(0, 4));
            var type = (MessageType)_hdr[4];

            var payload = new byte[payloadLen];
            if (payloadLen > 0 && !await ReadExactAsync(payload, ct).ConfigureAwait(false)) return;

            if (type == MessageType.DspSettings && payloadLen == 12)
            {
                var s = new DspSettingsPayload(
                    GainLinear: BinaryPrimitives.ReadSingleBigEndian(payload.AsSpan(0, 4)),
                    DbFloor:    BinaryPrimitives.ReadSingleBigEndian(payload.AsSpan(4, 4)),
                    DbCeiling:  BinaryPrimitives.ReadSingleBigEndian(payload.AsSpan(8, 4)));
                DspSettingsReceived?.Invoke(s);
            }
            // Unknown types are silently dropped — the length prefix means we
            // already consumed the right number of payload bytes.
        }
    }

    private async Task<bool> ReadExactAsync(byte[] buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = await _stream.ReadAsync(buf.AsMemory(got), ct).ConfigureAwait(false);
            if (n == 0) return false;   // peer closed
            got += n;
        }
        return true;
    }
}
