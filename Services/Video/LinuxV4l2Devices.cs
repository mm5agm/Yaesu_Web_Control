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

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int Ioctl(int fd, UIntPtr request, ref V4l2Capability arg);
    }
}
