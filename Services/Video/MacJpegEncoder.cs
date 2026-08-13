using System.Runtime.InteropServices;
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
        [ThreadStatic]
        private static byte[]? t_bgrRow;

        [ThreadStatic]
        private static byte[]? t_bgraRow;

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

            Mat? owned = null;
            Mat? resized = null;
            Mat? bgr = null;
            try
            {
                owned = bgrOrGray.Clone();
                var src = owned;

                if (maxWidth > 0 && src.Width > maxWidth)
                {
                    var scale = (double)maxWidth / src.Width;
                    var newH = Math.Max(1, (int)Math.Round(src.Height * scale));
                    resized = new Mat();
                    Cv2.Resize(src, resized, new OpenCvSharp.Size(maxWidth, newH), 0, 0, InterpolationFlags.Area);
                    src = resized;
                }

                var ch = src.Channels();
                if (ch == 4)
                {
                    bgr = new Mat();
                    Cv2.CvtColor(src, bgr, ColorConversionCodes.BGRA2BGR);
                    src = bgr;
                    ch = 3;
                }
                else if (ch == 1)
                {
                    bgr = new Mat();
                    Cv2.CvtColor(src, bgr, ColorConversionCodes.GRAY2BGR);
                    src = bgr;
                    ch = 3;
                }
                else if (ch != 3)
                {
                    skipReason = $"unsupported channel count {ch}";
                    return false;
                }

                if (src.Type() != MatType.CV_8UC3)
                {
                    skipReason = $"bad mat type {src.Type()}";
                    return false;
                }

                var w = src.Width;
                var h = src.Height;
                using var bitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
                if (!CopyBgrToBgra(src, bitmap))
                {
                    skipReason = "failed to copy pixels into SKBitmap";
                    return false;
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
            catch (Exception ex)
            {
                skipReason = ex.GetType().Name + ": " + ex.Message;
                jpegBytes = null;
                return false;
            }
            finally
            {
                bgr?.Dispose();
                resized?.Dispose();
                owned?.Dispose();
            }
        }

        private static bool CopyBgrToBgra(Mat bgr, SKBitmap bitmap)
        {
            var w = bgr.Width;
            var h = bgr.Height;
            if (bitmap.Width != w || bitmap.Height != h)
                return false;

            using var pixmap = bitmap.PeekPixels();
            if (pixmap is null)
                return false;

            var dstBase = pixmap.GetPixels();
            if (dstBase == IntPtr.Zero)
                return false;

            var srcStride = (int)bgr.Step();
            var dstStride = pixmap.RowBytes;
            var rowBytes = w * 3;
            var outBytes = w * 4;

            var bgrRow = t_bgrRow;
            if (bgrRow is null || bgrRow.Length < rowBytes)
            {
                bgrRow = new byte[rowBytes];
                t_bgrRow = bgrRow;
            }

            var bgraRow = t_bgraRow;
            if (bgraRow is null || bgraRow.Length < outBytes)
            {
                bgraRow = new byte[outBytes];
                t_bgraRow = bgraRow;
            }

            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(bgr.Data + (y * srcStride), bgrRow, 0, rowBytes);
                for (var x = 0; x < w; x++)
                {
                    var i = x * 3;
                    var o = x * 4;
                    bgraRow[o + 0] = bgrRow[i + 0]; // B
                    bgraRow[o + 1] = bgrRow[i + 1]; // G
                    bgraRow[o + 2] = bgrRow[i + 2]; // R
                    bgraRow[o + 3] = 255;
                }

                Marshal.Copy(bgraRow, 0, dstBase + (y * dstStride), outBytes);
            }

            return true;
        }
    }
}
