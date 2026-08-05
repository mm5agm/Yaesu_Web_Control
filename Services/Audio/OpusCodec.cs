using Concentus;
using Concentus.Enums;

namespace Yaesu_Web_Control.Services.Audio
{
    /// <summary>Thin Concentus wrapper fixed at 48 kHz mono / 20 ms frames.</summary>
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
                throw new ArgumentException("Need a full 20 ms frame.", nameof(pcm));
            return _encoder.Encode(pcm[..AudioConstants.FrameSamples], AudioConstants.FrameSamples, output, output.Length);
        }

        public byte[] Encode(ReadOnlySpan<float> pcm)
        {
            int n = Encode(pcm, _encodeBuf);
            var packet = new byte[n];
            _encodeBuf.AsSpan(0, n).CopyTo(packet);
            return packet;
        }

        public int Decode(ReadOnlySpan<byte> packet, Span<float> pcmOut, bool decodeFec = false)
        {
            return _decoder.Decode(packet, pcmOut, AudioConstants.FrameSamples, decodeFec);
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
