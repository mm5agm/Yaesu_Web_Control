namespace Yaesu_Web_Control.Services.Audio
{
    public sealed record AudioDeviceInfo(int Index, string Name, int MaxInputChannels, int MaxOutputChannels, double DefaultSampleRate)
    {
        public bool IsInput => MaxInputChannels > 0;
        public bool IsOutput => MaxOutputChannels > 0;
    }
}
