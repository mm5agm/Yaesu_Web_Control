// Yaesu Web Control — the FT-710's own scope data, as it arrives over USB.
//
// The FT-710 has an FTDI FT4222H USB-to-SPI bridge inside it. With menu item
// EX 03-01-26 SCU-LAN10 switched ON (no SCU-LAN10 unit is needed, only the
// menu switch) the bridge appears as a third USB device beside the CAT serial
// ports and the sound card, and streams the radio's own spectrum scope data.
// This file is the pure half: it turns the byte stream into bins and says
// where those bins sit in frequency. It has no FTDI code in it, so it builds
// and is tested on every platform. The FTDI half is Ft4222Bridge in the SDR
// worker.
//
// Nothing here was measured by me. The frame layout, the trailer, the
// inverted byte scale and the left-to-right order were all established on
// real FT-710s by other GPL-3 projects, and this is a C# port of their
// findings:
//
//   * kd9taw/Nexus, crates/tempo-audio/src/yaesu_wf.rs (GPL-3.0), with the
//     bench measurements made by ON8ST on his FT-710, 2026-08-17 to 08-20.
//   * ratmandu/YaesuWFTesting (MIT), the first published frame layout.
//
// The test fixture Tests/YaesuWebControl.Tests/Fixtures/ft710_wf_frame.bin is
// ON8ST's captured frame, taken from the Nexus repository (GPL-3.0).
//
// Protocol-level: nothing here has been checked against a radio by me. See
// docs/design/ft710-native-scope.md for the bench checklist.

namespace Yaesu_Web_Control.Services.Sdr;

public static class Ft710ScopeFrame
{
    /// <summary>
    /// Device key prefix for the radio's own scope in the SDR device
    /// dropdowns. Anything else is an SDRplay or SoapySDR key.
    /// </summary>
    public const string KeyPrefix = "yaesu-scope:";

    /// <summary>The one key there is today: the FT-710's internal FT4222.</summary>
    public const string Ft710Key = KeyPrefix + "ft710";

    public static bool IsScopeKey(string? key) =>
        key is not null && key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Does this radio model have the internal FT4222 bridge? Only the FT-710
    /// is confirmed. The FTdx10 and FTdx101 put similar data on the rear ACC
    /// socket, which would need an external USB-to-SPI adapter; not handled.
    /// </summary>
    public static bool ModelHasBridge(string? model) =>
        string.Equals(model, "FT-710", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Bytes per frame. The radio does not frame or delimit the stream beyond
    /// the trailer below.
    /// </summary>
    public const int FrameBytes = 4096;

    /// <summary>
    /// Live bins in the MAIN receiver's waterfall row, at offset 0. The row's
    /// layout stride is 852, but the last two bytes are always zero, and
    /// inverted they would read as a full-scale carrier at the top edge of
    /// every span.
    /// </summary>
    public const int Bins = 850;

    /// <summary>
    /// The 16 bytes that end every frame: FF 01 EE 01, four times. Measured
    /// (Nexus/ON8ST), not documented by Yaesu. It is what lets a frame be cut
    /// out of the byte stream: the read window drifts against the radio's
    /// frame boundary on its own, and a misaligned window puts every bin at
    /// the wrong frequency.
    /// </summary>
    public static ReadOnlySpan<byte> Trailer =>
    [
        0xFF, 0x01, 0xEE, 0x01, 0xFF, 0x01, 0xEE, 0x01,
        0xFF, 0x01, 0xEE, 0x01, 0xFF, 0x01, 0xEE, 0x01,
    ];

    /// <summary>
    /// End index (exclusive) of the LAST complete frame in the buffer, or -1.
    /// "Complete" means the trailer has a whole frame's worth of bytes in
    /// front of it. The last one rather than the first keeps latency down
    /// when a read has caught up on more than one frame.
    /// </summary>
    public static int LastFrameEnd(ReadOnlySpan<byte> buffer)
    {
        var trailer = Trailer;
        int found = -1;
        int from  = 0;
        while (from + trailer.Length <= buffer.Length)
        {
            int rel = buffer[from..].IndexOf(trailer);
            if (rel < 0) break;
            int end = from + rel + trailer.Length;
            if (end >= FrameBytes) found = end;
            from += rel + 1;
        }
        return found;
    }

    /// <summary>
    /// The MAIN waterfall row of one frame as display levels in dB, LOW to
    /// HIGH frequency left to right — the radio's order, NOT the inverted
    /// order of the 9 MHz IF tap that the SDR panel normally flips.
    ///
    /// The radio's bytes run the wrong way for a level: a LOW byte is a
    /// STRONG signal (noise sits near 185, a strong carrier near 109). They
    /// are turned the right way up here and put on a dB-like scale so the
    /// panel's auto floor and Range slider behave as they do for an SDR.
    /// </summary>
    /// <exception cref="ArgumentException">The frame is not 4096 bytes.</exception>
    public static float[] ParseMainRowDb(ReadOnlySpan<byte> frame)
    {
        // A short frame is dropped by the caller, never padded: padding would
        // draw an invented floor at one end of the span.
        if (frame.Length != FrameBytes)
            throw new ArgumentException($"expected {FrameBytes} bytes, got {frame.Length}", nameof(frame));

        var row = new float[Bins];
        for (int i = 0; i < Bins; i++)
            row[i] = ByteToDb(frame[i]);
        return row;
    }

    /// <summary>
    /// dB per step of the radio's byte. NOT MEASURED: nobody has yet
    /// compared the byte against a known level. Half a dB per step puts the
    /// FT-710's usual noise-to-strong-carrier distance (about 76 steps) at
    /// 38 dB, which looks like the radio's own screen. The panel scales
    /// vertically for itself, so a wrong value here stretches or squashes the
    /// trace; it never moves a signal sideways.
    /// </summary>
    public const float DbPerStep = 0.5f;

    /// <summary>The level a raw 0 (the strongest the radio can report) maps to.</summary>
    public const float DbAtFullScale = -20f;

    public static float ByteToDb(byte raw) => DbAtFullScale - raw * DbPerStep;

    /// <summary>
    /// Cuts complete frames out of the byte stream. One per bridge; not
    /// thread-safe, the read loop owns it.
    /// </summary>
    public sealed class Assembler
    {
        // Three frames is enough to cut one out at any rotation. More than
        // that means trailers are not being found at all, and holding a
        // growing buffer of a stream that cannot be parsed helps nobody.
        public const int Capacity = 3 * FrameBytes;

        private readonly byte[] _buf = new byte[Capacity];
        private int _len;

        public int Buffered => _len;

        /// <summary>
        /// Adds bytes from one read and returns the newest complete frame, or
        /// null when there is none yet. Bytes up to the end of the returned
        /// frame are consumed, so the same frame is never cut twice.
        /// </summary>
        public byte[]? Push(ReadOnlySpan<byte> data)
        {
            if (data.Length >= Capacity)
            {
                data[^Capacity..].CopyTo(_buf);
                _len = Capacity;
            }
            else
            {
                int overflow = _len + data.Length - Capacity;
                if (overflow > 0)
                {
                    Buffer.BlockCopy(_buf, overflow, _buf, 0, _len - overflow);
                    _len -= overflow;
                }
                data.CopyTo(_buf.AsSpan(_len));
                _len += data.Length;
            }

            int end = LastFrameEnd(_buf.AsSpan(0, _len));
            if (end < 0) return null;

            var frame = _buf.AsSpan(end - FrameBytes, FrameBytes).ToArray();
            Buffer.BlockCopy(_buf, end, _buf, 0, _len - end);
            _len -= end;
            return frame;
        }
    }
}
