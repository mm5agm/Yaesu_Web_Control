namespace Yaesu_Web_Control.Services.Audio
{
    /// <summary>Shared constants for the remote radio audio bridge.</summary>
    public static class AudioConstants
    {
        public const int SampleRate = 48_000;
        public const int Channels = 1;
        /// <summary>20 ms at 48 kHz.</summary>
        public const int FrameSamples = 960;
        public const int OpusBitrate = 32_000;

        public const byte MsgOpusRx = 0x01;
        public const byte MsgOpusTx = 0x02;
        public const byte MsgPcmRx = 0x03;
        public const byte MsgPcmTx = 0x04;
        public const byte MsgControl = 0x10;

        public const string CodecOpus = "opus";
        public const string CodecPcm16 = "pcm16";
    }
}
