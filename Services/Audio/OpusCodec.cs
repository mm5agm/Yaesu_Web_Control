using Concentus;
using Concentus.Enums;

namespace Yaesu_Web_Control.Services.Audio
{
    /// <summary>
    /// Thin Concentus wrapper at 48 kHz mono.
    /// Encodes 10 ms frames; decode accepts up to Opus max packet duration
    /// (WebCodecs defaults to 20 ms unless configured otherwise).
    /// </summary>
    public sealed class OpusCodec : IDisposable
    {
        private readonly IOpusEncoder _encoder;
        private readonly IOpusDecoder _decoder;
        private readonly byte[] _encodeBuf = new byte[4000];
        private bool _disposed;

        public OpusCodec()
        {
            _encoder = OpusCodecFactory.CreateEncoder(
                AudioConstants.SampleRate,
                AudioConstants.Channels,
                OpusApplication.OPUS_APPLICATION_VOIP);
            _encoder.Bitrate = AudioConstants.OpusBitrate;
            _encoder.Complexity = 5;

            _decoder = OpusCodecFactory.CreateDecoder(
                AudioConstants.SampleRate,
                AudioConstants.Channels);
        }

        public int Encode(ReadOnlySpan<float> pcm, Span<byte> output)
        {
            if (pcm.Length < AudioConstants.FrameSamples)
                throw new ArgumentException("Need a full 10 ms frame.", nameof(pcm));
            return _encoder.Encode(pcm[..AudioConstants.FrameSamples], AudioConstants.FrameSamples, output, output.Length);
        }

        public byte[] Encode(ReadOnlySpan<float> pcm)
        {
            int n = Encode(pcm, _encodeBuf);
            var packet = new byte[n];
            _encodeBuf.AsSpan(0, n).CopyTo(packet);
            return packet;
        }

        /// <summary>
        /// Decodes one Opus packet. <paramref name="pcmOut"/> must hold at least 960
        /// samples (20 ms); prefer <see cref="AudioConstants.OpusDecodeMaxSamples"/>.
        /// Returns samples written.
        /// </summary>
        public int Decode(ReadOnlySpan<byte> packet, Span<float> pcmOut, bool decodeFec = false)
        {
            if (pcmOut.Length < 960)
                throw new ArgumentException("Decode buffer must hold at least 20 ms (960 samples).", nameof(pcmOut));

            // frame_size is available space — must cover the packet duration (WebCodecs Opus
            // defaults to 20 ms / 960 samples). Using the Opus maximum keeps us safe.
            int frameSize = Math.Min(pcmOut.Length, AudioConstants.OpusDecodeMaxSamples);
            return _decoder.Decode(packet, pcmOut[..frameSize], frameSize, decodeFec);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            (_encoder as IDisposable)?.Dispose();
            (_decoder as IDisposable)?.Dispose();
        }
    }
}
