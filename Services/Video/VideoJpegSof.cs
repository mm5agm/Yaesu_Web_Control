using System.Runtime.InteropServices;

namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// JPEG Start-Of-Frame size. V4L2 MJPEG with CONVERT_RGB=0 is a 1-row
    /// bitstream Mat whose <c>Width</c> is the compressed byte length — not
    /// the picture size the Radio Display panel should show.
    /// </summary>
    internal static class VideoJpegSof
    {
        public static bool TryReadSize(byte[] jpeg, out int width, out int height) =>
            TryReadSize(jpeg.AsSpan(), out width, out height);

        public static bool TryReadSize(IntPtr data, int length, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (data == IntPtr.Zero || length < 12)
                return false;

            var n = Math.Min(length, 65_536);
            var heap = new byte[n];
            Marshal.Copy(data, heap, 0, n);
            return TryReadSize(heap.AsSpan(), out width, out height);
        }

        public static bool TryReadSize(ReadOnlySpan<byte> jpeg, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (jpeg.Length < 12 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
                return false;

            var i = 2;
            while (i + 8 < jpeg.Length)
            {
                if (jpeg[i] != 0xFF)
                {
                    i++;
                    continue;
                }

                var marker = jpeg[i + 1];
                if (marker == 0xFF)
                {
                    i++;
                    continue;
                }

                if (marker is 0xD8 or 0xD9 or >= 0xD0 and <= 0xD7)
                {
                    i += 2;
                    continue;
                }

                if (i + 3 >= jpeg.Length)
                    return false;

                var segLen = (jpeg[i + 2] << 8) | jpeg[i + 3];
                if (segLen < 2)
                    return false;

                // SOF0–SOF3 (baseline / extended / progressive / lossless).
                if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3)
                {
                    if (i + 8 >= jpeg.Length)
                        return false;
                    height = (jpeg[i + 5] << 8) | jpeg[i + 6];
                    width = (jpeg[i + 7] << 8) | jpeg[i + 8];
                    return width is >= 2 and <= 7680 && height is >= 2 and <= 4320;
                }

                i += 2 + segLen;
            }

            return false;
        }
    }
}
