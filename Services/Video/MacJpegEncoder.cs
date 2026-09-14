using OpenCvSharp;
using SkiaSharp;

namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// OpenCvSharp's embedded libjpeg can SIGSEGV via <c>error_exit</c> →
    /// <c>longjmp</c> on macOS AVFoundation frames (managed try/catch cannot
    /// catch it). Encode with SkiaSharp instead — already shipped for Avalonia.
    /// </summary>
    internal static class MacJpegEncoder
    {
        public static bool TryEncode(
            Mat bgrOrGray,
            int maxWidth,
            int jpegQuality,
            out byte[]? jpegBytes,
            out int outW,
            out int outH,
            out string? skipReason)
        {
            jpegBytes = null;
            outW = 0;
            outH = 0;
            skipReason = null;

            if (bgrOrGray.Empty() || bgrOrGray.Width < 2 || bgrOrGray.Height < 2)
            {
                skipReason = "empty or tiny frame";
                return false;
            }

            Mat? resized = null;
            try
            {
                var src = bgrOrGray;

                if (maxWidth > 0 && src.Width > maxWidth)
                {
                    var scale = (double)maxWidth / src.Width;
                    var newH = Math.Max(1, (int)Math.Round(src.Height * scale));
                    resized = new Mat();
                    // Linear is much cheaper than Area at 1080p→800 and is
                    // sharp enough for a radio LCD. Area was a measurable
                    // slice of the ~20 fps ceiling.
                    Cv2.Resize(src, resized, new OpenCvSharp.Size(maxWidth, newH), 0, 0, InterpolationFlags.Linear);
                    src = resized;
                }

                var ch = src.Channels();
                ColorConversionCodes? toBgra = ch switch
                {
                    1 => ColorConversionCodes.GRAY2BGRA,
                    3 => ColorConversionCodes.BGR2BGRA,
                    4 => null,
                    _ => throw new InvalidOperationException($"unsupported channel count {ch}")
                };

                if (toBgra is null && src.Type() != MatType.CV_8UC4)
                {
                    skipReason = $"bad mat type {src.Type()}";
                    return false;
                }

                if (toBgra is not null && src.Type() != MatType.CV_8UC1 && src.Type() != MatType.CV_8UC3)
                {
                    skipReason = $"bad mat type {src.Type()}";
                    return false;
                }

                var w = src.Width;
                var h = src.Height;
                using var bitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
                using var pixmap = bitmap.PeekPixels();
                if (pixmap is null)
                {
                    skipReason = "failed to lock SKBitmap pixels";
                    return false;
                }

                var dstPtr = pixmap.GetPixels();
                if (dstPtr == IntPtr.Zero)
                {
                    skipReason = "SKBitmap pixel pointer was null";
                    return false;
                }

                // Write BGRA straight into the Skia bitmap — OpenCV SIMD
                // CvtColor, no per-pixel C# loop and no extra BGRA Mat.
                using (var wrapped = Mat.FromPixelData(h, w, MatType.CV_8UC4, dstPtr, pixmap.RowBytes))
                {
                    if (toBgra is { } code)
                        Cv2.CvtColor(src, wrapped, code);
                    else
                        src.CopyTo(wrapped);
                }

                var quality = Math.Clamp(jpegQuality, 1, 100);
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
                if (data is null || data.Size < 100)
                {
                    skipReason = "Skia JPEG encode returned empty buffer";
                    return false;
                }

                jpegBytes = data.ToArray();
                if (jpegBytes[0] != 0xFF || jpegBytes[1] != 0xD8)
                {
                    skipReason = "Skia JPEG missing SOI";
                    jpegBytes = null;
                    return false;
                }

                outW = w;
                outH = h;
                return true;
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("unsupported channel", StringComparison.Ordinal))
            {
                skipReason = ex.Message;
                jpegBytes = null;
                return false;
            }
            catch (Exception ex)
            {
                skipReason = ex.GetType().Name + ": " + ex.Message;
                jpegBytes = null;
                return false;
            }
            finally
            {
                resized?.Dispose();
            }
        }
    }
}
