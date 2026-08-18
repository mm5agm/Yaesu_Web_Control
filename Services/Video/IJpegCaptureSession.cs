namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// Compressed-MJPEG capture that copies JPEG bytes as-is (DirectShow pin
    /// or Media Foundation source reader). OpenCV DirectShow is not this —
    /// it always decodes to BGR.
    /// </summary>
    internal interface IJpegCaptureSession : IDisposable
    {
        int Width { get; }
        int Height { get; }
        double DeviceFps { get; }
        bool TryReadJpeg(out byte[]? jpeg);
        bool TrySetFrameRate(int targetFps);

        /// <summary>
        /// True once the device has been seen packing several pictures into one
        /// sample. Some HDMI grabbers do this in every mode that runs through
        /// their internal scaler: the JPEG is well formed but its scan holds more
        /// than one frame, which renders as a tiled, repeated picture. Capture
        /// switches to the device's native mode when this is reported.
        /// </summary>
        bool DeviceMergesFrames => false;
    }
}
