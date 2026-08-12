namespace Yaesu_Web_Control.Services.Audio
{
    /// <summary>Shared constants for the remote radio audio bridge.</summary>
    public static class AudioConstants
    {
        public const int SampleRate = 48_000;
        public const int Channels = 1;
        /// <summary>10 ms at 48 kHz — PCM packetization / Opus encode frame.</summary>
        public const int FrameSamples = 480;
        /// <summary>
        /// WebCodecs Opus defaults to 20 ms; Concentus decode buffer must be at least
        /// this large or Decode throws and the audio WebSocket dies.
        /// </summary>
        public const int OpusDecodeMaxSamples = 5760; // 120 ms @ 48 kHz (Opus max)
        public const int OpusBitrate = 32_000;

        /// <summary>Max host TX ring depth (~80 ms) before dropping oldest samples.</summary>
        public const int PlaybackRingMaxSamples = FrameSamples * 8;

        public const byte MsgOpusRx = 0x01;
        public const byte MsgOpusTx = 0x02;
        public const byte MsgPcmRx = 0x03;
        public const byte MsgPcmTx = 0x04;
        public const byte MsgControl = 0x10;

        public const string CodecOpus = "opus";
        public const string CodecPcm16 = "pcm16";
    }
}
