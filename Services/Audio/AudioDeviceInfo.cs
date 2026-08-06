namespace Yaesu_Web_Control.Services.Audio
{
    public sealed record AudioDeviceInfo(
        int Index,
        string Name,
        string HostApiName,
        int HostApiIndex,
        int MaxInputChannels,
        int MaxOutputChannels,
        double DefaultSampleRate)
    {
        public bool IsInput => MaxInputChannels > 0;
        public bool IsOutput => MaxOutputChannels > 0;

        /// <summary>Human-readable label and persistence value (name + host API).</summary>
        public string DisplayName =>
            string.IsNullOrEmpty(HostApiName) ? Name : AudioDeviceKey.Format(Name, HostApiName);

        /// <summary>Same as <see cref="DisplayName"/> — the string written to settings.</summary>
        public string PersistenceKey => DisplayName;
    }
}
