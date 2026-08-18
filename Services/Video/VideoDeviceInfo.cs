namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>One UVC / V4L2 / AVFoundation / Media Foundation capture device.</summary>
    public sealed class VideoDeviceInfo
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public int Index { get; init; }

        /// <summary>Advertised capture rates (sorted), or empty if unknown.</summary>
        public int[] Rates { get; init; } = [];

        public int MaxFps => VideoFpsOptions.Max(Rates);
    }
}
