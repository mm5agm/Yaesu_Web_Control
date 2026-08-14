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
    }
}
