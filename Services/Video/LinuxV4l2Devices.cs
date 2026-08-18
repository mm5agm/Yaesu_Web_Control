using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// V4L2 helpers for Linux enumeration. Sysfs lists every video4linux node
    /// (UVC metadata, Pi codec/ISP) even when the matching /dev/videoN is not
    /// mapped into Docker — OpenCV then fails with "could not open index N".
    /// </summary>
    [SupportedOSPlatform("linux")]
    internal static class LinuxV4l2Devices
    {
        private const int O_RDONLY = 0;
        private const int O_NONBLOCK = 0x800;
        private const uint V4L2_CAP_VIDEO_CAPTURE = 0x00000001;
        private const uint V4L2_CAP_VIDEO_CAPTURE_MPLANE = 0x00001000;
        private const uint V4L2_CAP_DEVICE_CAPS = 0x80000000;
        private const int EACCES = 13;
        private const int EBUSY = 16;

        // _IOR('V', 0, struct v4l2_capability) — 104-byte struct, READ.
        private static readonly UIntPtr VidIocQueryCap = unchecked((UIntPtr)0x80685600);

        public static bool DeviceNodeExists(int index) =>
            File.Exists($"/dev/video{index}");

        public static string DevicePath(int index) => $"/dev/video{index}";

        /// <summary>
        /// Human-readable reason OpenCV could not open this index (permissions,
        /// missing Docker mapping, UVC metadata node, or native-library load).
        /// </summary>
        public static string DescribeOpenFailure(int index, string? nativeError)
        {
            if (!string.IsNullOrEmpty(nativeError) && LooksLikeNativeLoadFailure(nativeError))
                return nativeError;

            var path = DevicePath(index);
            if (!File.Exists(path))
            {
                return $"Could not open {path}: device node is not present. " +
                       "Map /dev/video0 and /dev/video1 into Docker.";
            }

            var fd = Open(path, O_RDONLY | O_NONBLOCK);
            if (fd < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == EACCES)
                {
                    return $"Permission denied opening {path}. " +
                           "Add the host video group via YWC_VIDEO_GID (getent group video).";
                }

                if (errno == EBUSY)
                    return $"{path} is busy (another process holds the camera).";

                return $"Could not open {path} (errno {errno}).";
            }

            try
            {
                var cap = new V4l2Capability();
                if (Ioctl(fd, VidIocQueryCap, ref cap) == 0)
                {
                    var caps = (cap.Capabilities & V4L2_CAP_DEVICE_CAPS) != 0
                        ? cap.DeviceCaps
                        : cap.Capabilities;
                    var isCapture = (caps & (V4L2_CAP_VIDEO_CAPTURE | V4L2_CAP_VIDEO_CAPTURE_MPLANE)) != 0;
                    if (!isCapture)
                    {
                        return $"{path} is not a V4L2 capture node (UVC metadata). " +
                               "Select USB Video (video0).";
                    }
                }
            }
            finally
            {
                _ = Close(fd);
            }

            if (!string.IsNullOrEmpty(nativeError))
                return $"Could not open {path}: {nativeError}";

            return $"Could not open capture device {path}.";
        }

        private static bool LooksLikeNativeLoadFailure(string error) =>
            error.Contains("OpenCvSharpExtern", StringComparison.OrdinalIgnoreCase)
            || error.Contains("libgomp", StringComparison.OrdinalIgnoreCase)
            || error.Contains("libopencv", StringComparison.OrdinalIgnoreCase)
            || error.Contains("shared library", StringComparison.OrdinalIgnoreCase)
            || error.Contains("DllNotFound", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Pi SoC m2m/codec nodes are not USB capture devices.
        /// </summary>
        public static bool IsNonCaptureName(string? friendly)
        {
            if (string.IsNullOrWhiteSpace(friendly))
                return false;

            return friendly.StartsWith("bcm2835-", StringComparison.OrdinalIgnoreCase)
                   || friendly.StartsWith("rpivid", StringComparison.OrdinalIgnoreCase)
                   || friendly.StartsWith("pisp", StringComparison.OrdinalIgnoreCase)
                   || friendly.StartsWith("rpi-hevc", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when this node should appear in the Radio Display dropdown.
        /// Missing /dev node → false. Metadata / m2m → false. Permission or
        /// busy → true so the operator still sees the camera.
        /// </summary>
        public static bool ShouldList(int index, string? friendly)
        {
            if (!DeviceNodeExists(index) || IsNonCaptureName(friendly))
                return false;

            return TryIsCaptureNode(index, out var isCapture) ? isCapture : true;
        }

        private static bool TryIsCaptureNode(int index, out bool isCapture)
        {
            isCapture = false;
            var fd = Open($"/dev/video{index}", O_RDONLY | O_NONBLOCK);
            if (fd < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno is EACCES or EBUSY)
                    return false;
                return true;
            }

            try
            {
                var cap = new V4l2Capability();
                if (Ioctl(fd, VidIocQueryCap, ref cap) != 0)
                    return false;

                var caps = (cap.Capabilities & V4L2_CAP_DEVICE_CAPS) != 0
                    ? cap.DeviceCaps
                    : cap.Capabilities;
                isCapture = (caps & (V4L2_CAP_VIDEO_CAPTURE | V4L2_CAP_VIDEO_CAPTURE_MPLANE)) != 0;
                return true;
            }
            finally
            {
                _ = Close(fd);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct V4l2Capability
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
            public string Driver;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string Card;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string BusInfo;
            public uint Version;
            public uint Capabilities;
            public uint DeviceCaps;
            public uint Reserved0;
            public uint Reserved1;
            public uint Reserved2;
        }

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int Open(string pathname, int flags);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int Close(int fd);

        // _IOWR('V', 2, struct v4l2_fmtdesc) — 64 bytes
        private static readonly UIntPtr VidIocEnumFmt = unchecked((UIntPtr)0xC0405602);
        // _IOWR('V', 74, struct v4l2_frmsizeenum) — 44 bytes
        private static readonly UIntPtr VidIocEnumFramesizes = unchecked((UIntPtr)0xC02C564A);
        // _IOWR('V', 75, struct v4l2_frmivalenum) — 52 bytes
        private static readonly UIntPtr VidIocEnumFrameintervals = unchecked((UIntPtr)0xC034564B);
        // _IOWR('V', 21/22, struct v4l2_streamparm) — 204 bytes
        private static readonly UIntPtr VidIocGParm = unchecked((UIntPtr)0xC0CC5615);
        private static readonly UIntPtr VidIocSParm = unchecked((UIntPtr)0xC0CC5616);

        private const uint V4l2BufTypeVideoCapture = 1;
        private const uint V4l2BufTypeVideoCaptureMplane = 9;
        private const uint V4l2FrmSizeDiscrete = 1;
        private const uint V4l2FrmSizeStepwise = 3;
        private const uint V4l2FrmIvalDiscrete = 1;
        private const uint V4l2FrmIvalContinuous = 2;
        private const uint V4l2FrmIvalStepwise = 3;
        private const int EnumCap = 32;

        private static uint FourCc(char a, char b, char c, char d) =>
            (uint)(a | (b << 8) | (c << 16) | (d << 24));

        private static bool IsJpegPixelFormat(uint pixelFormat) =>
            pixelFormat == FourCc('M', 'J', 'P', 'G') || pixelFormat == FourCc('J', 'P', 'E', 'G');

        /// <summary>
        /// Advertised capture rates via ENUM_FMT / FRAMESIZES / FRAMEINTERVALS.
        /// Empty if the node is busy or the driver does not report intervals.
        /// Does not start streaming.
        /// </summary>
        public static int[] TryQueryFpsRates(int index) =>
            VideoFpsOptions.UniqueSorted(TryQueryPins(index).Select(p => p.Fps));

        public static int TryQueryMaxFps(int index) =>
            VideoFpsOptions.Max(TryQueryFpsRates(index));

        public static List<VideoCapturePinRank.Pin> TryQueryPins(int index)
        {
            var fd = Open(DevicePath(index), O_RDONLY | O_NONBLOCK);
            if (fd < 0)
                return [];

            try
            {
                var pins = new List<VideoCapturePinRank.Pin>();
                CollectPinsForBufType(fd, V4l2BufTypeVideoCapture, pins);
                if (pins.Count == 0)
                    CollectPinsForBufType(fd, V4l2BufTypeVideoCaptureMplane, pins);
                return pins;
            }
            finally
            {
                _ = Close(fd);
            }
        }

        /// <summary>
        /// VIDIOC_S_PARM timeperframe. OpenCV's CAP_PROP_FPS often does not
        /// stick on V4L2 after the first S_FMT.
        /// </summary>
        public static bool TrySetFrameRate(int index, int fps)
        {
            if (fps < 1)
                return false;

            var fd = Open(DevicePath(index), O_RDONLY | O_NONBLOCK);
            if (fd < 0)
                return false;

            try
            {
                return TrySetFrameRateOnFd(fd, V4l2BufTypeVideoCapture, fps)
                    || TrySetFrameRateOnFd(fd, V4l2BufTypeVideoCaptureMplane, fps);
            }
            finally
            {
                _ = Close(fd);
            }
        }

        private static bool TrySetFrameRateOnFd(int fd, uint bufType, int fps)
        {
            var parm = new V4l2StreamParm { Type = bufType };
            if (Ioctl(fd, VidIocGParm, ref parm) != 0)
                return false;

            parm.TpfNumerator = 1;
            parm.TpfDenominator = (uint)fps;
            return Ioctl(fd, VidIocSParm, ref parm) == 0;
        }

        private static void CollectPinsForBufType(int fd, uint bufType, List<VideoCapturePinRank.Pin> dest)
        {
            for (uint fmtIndex = 0; fmtIndex < EnumCap; fmtIndex++)
            {
                var fmt = new V4l2FmtDesc { Index = fmtIndex, Type = bufType };
                if (Ioctl(fd, VidIocEnumFmt, ref fmt) != 0 || fmt.PixelFormat == 0)
                    break;

                var jpeg = IsJpegPixelFormat(fmt.PixelFormat);
                for (uint sizeIndex = 0; sizeIndex < EnumCap; sizeIndex++)
                {
                    var size = new V4l2FrmSizeEnum
                    {
                        Index = sizeIndex,
                        PixelFormat = fmt.PixelFormat
                    };
                    if (Ioctl(fd, VidIocEnumFramesizes, ref size) != 0)
                        break;

                    if (size.Type == V4l2FrmSizeDiscrete)
                    {
                        CollectPinIntervals(fd, fmt.PixelFormat, size.U0, size.U1, jpeg, dest);
                    }
                    else if (size.Type == V4l2FrmSizeStepwise)
                    {
                        CollectPinIntervals(fd, fmt.PixelFormat, size.U0, size.U3, jpeg, dest);
                        CollectPinIntervals(fd, fmt.PixelFormat, size.U1, size.U4, jpeg, dest);
                    }
                }
            }
        }

        private static void CollectPinIntervals(
            int fd, uint pixelFormat, uint width, uint height, bool jpeg,
            List<VideoCapturePinRank.Pin> dest)
        {
            if (width < 2 || height < 2)
                return;

            for (uint i = 0; i < EnumCap; i++)
            {
                var ival = new V4l2FrmIvalEnum
                {
                    Index = i,
                    PixelFormat = pixelFormat,
                    Width = width,
                    Height = height
                };
                if (Ioctl(fd, VidIocEnumFrameintervals, ref ival) != 0)
                    break;

                if (ival.Type == V4l2FrmIvalDiscrete)
                {
                    dest.Add(new VideoCapturePinRank.Pin((int)width, (int)height, IntervalToFps(ival.U0, ival.U1), jpeg));
                }
                else if (ival.Type is V4l2FrmIvalContinuous or V4l2FrmIvalStepwise)
                {
                    var fastest = IntervalToFps(ival.U0, ival.U1);
                    var slowest = IntervalToFps(ival.U2, ival.U3);
                    if (slowest <= 0)
                        slowest = fastest;
                    dest.Add(new VideoCapturePinRank.Pin((int)width, (int)height, fastest, jpeg));
                    dest.Add(new VideoCapturePinRank.Pin((int)width, (int)height, slowest, jpeg));
                    var extras = new List<double>();
                    VideoFpsOptions.AddPresetsInRange(extras, slowest, fastest);
                    foreach (var fps in extras)
                        dest.Add(new VideoCapturePinRank.Pin((int)width, (int)height, fps, jpeg));
                }
            }
        }

        private static double IntervalToFps(uint numerator, uint denominator) =>
            numerator == 0 ? 0 : (double)denominator / numerator;

        [StructLayout(LayoutKind.Sequential)]
        private struct V4l2FmtDesc
        {
            public uint Index;
            public uint Type;
            public uint Flags;
            public uint D0, D1, D2, D3, D4, D5, D6, D7;
            public uint PixelFormat;
            public uint MbusCode;
            public uint Reserved0, Reserved1, Reserved2;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct V4l2FrmSizeEnum
        {
            public uint Index;
            public uint PixelFormat;
            public uint Type;
            public uint U0, U1, U2, U3, U4, U5;
            public uint Reserved0, Reserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct V4l2FrmIvalEnum
        {
            public uint Index;
            public uint PixelFormat;
            public uint Width;
            public uint Height;
            public uint Type;
            public uint U0, U1, U2, U3, U4, U5;
            public uint Reserved0, Reserved1;
        }

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int Ioctl(int fd, UIntPtr request, ref V4l2Capability arg);

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int Ioctl(int fd, UIntPtr request, ref V4l2FmtDesc arg);

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int Ioctl(int fd, UIntPtr request, ref V4l2FrmSizeEnum arg);

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int Ioctl(int fd, UIntPtr request, ref V4l2FrmIvalEnum arg);

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int Ioctl(int fd, UIntPtr request, ref V4l2StreamParm arg);

        [StructLayout(LayoutKind.Sequential, Size = 204)]
        private struct V4l2StreamParm
        {
            public uint Type;
            public uint Capability;
            public uint CaptureMode;
            public uint TpfNumerator;
            public uint TpfDenominator;
            public uint ExtendedMode;
            public uint ReadBuffers;
        }
    }
}
