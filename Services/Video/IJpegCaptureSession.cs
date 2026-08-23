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
        /// rebuilds the graph on the same mode when this is reported, and only
        /// falls back to the device's native mode if the rebuilt one merges too
        /// (#132: the fault is usually the first graph, not the mode).
        /// Implemented by the Windows DirectShow session only; the AVFoundation
        /// and V4L2 sessions have never been seen to do it.
        /// </summary>
        bool DeviceMergesFrames => false;
    }
}
