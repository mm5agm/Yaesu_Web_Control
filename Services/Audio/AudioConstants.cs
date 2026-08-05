namespace Yaesu_Web_Control.Services.Audio
{
    /// <summary>Shared constants for the remote radio audio bridge.</summary>
    public static class AudioConstants
    {
        public const int SampleRate = 48_000;
        public const int Channels = 1;
        /// <summary>10 ms at 48 kHz — lower packetization delay than 20 ms.</summary>
        public const int FrameSamples = 480;
        public const int OpusBitrate = 32_000;

        /// <summary>Max host TX ring depth (~40 ms) before dropping oldest samples.</summary>
        public const int PlaybackRingMaxSamples = FrameSamples * 4;

        public const byte MsgOpusRx = 0x01;
        public const byte MsgOpusTx = 0x02;
        public const byte MsgPcmRx = 0x03;
        public const byte MsgPcmTx = 0x04;
        public const byte MsgControl = 0x10;

        public const string CodecOpus = "opus";
        public const string CodecPcm16 = "pcm16";
    }
}
