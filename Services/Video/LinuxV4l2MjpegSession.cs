using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// Native V4L2 MJPEG mmap capture. OpenCV's V4L2 backend renegotiates
    /// 800×600@30 to YUYV@20 (USB2 uncompressed cap) even when the dongle
    /// advertises MJPEG 800×600 at 30/50/60.
    /// </summary>
    [SupportedOSPlatform("linux")]
    internal sealed class LinuxV4l2MjpegSession : IJpegCaptureSession
    {
        private const int BufferCount = 4;
        private const int Eagain = 11;
        private const int ProtRead = 1;
        private const int ProtWrite = 2;
        private const int MapShared = 1;
        private const uint MemoryMmap = 1;
        private const uint BufTypeCapture = 1;
        private static readonly IntPtr MapFailed = new(-1);

        // _IOWR('V', 8, v4l2_requestbuffers=20)
        private static readonly UIntPtr VidIocReqbufs = unchecked((UIntPtr)0xC0145608);
        // _IOWR('V', 9/15/17, v4l2_buffer=88) on 64-bit
        private static readonly UIntPtr VidIocQuerybuf = unchecked((UIntPtr)0xC0585609);
        private static readonly UIntPtr VidIocQbuf = unchecked((UIntPtr)0xC058560F);
        private static readonly UIntPtr VidIocDqbuf = unchecked((UIntPtr)0xC0585611);
        // _IOW('V', 18/19, int)
        private static readonly UIntPtr VidIocStreamon = unchecked((UIntPtr)0x40045612);
        private static readonly UIntPtr VidIocStreamoff = unchecked((UIntPtr)0x40045613);

        private readonly int _fd;
        private readonly MappedBuffer[] _buffers;
        private readonly ILogger _logger;
        private bool _streaming;
        private bool _disposed;

        private LinuxV4l2MjpegSession(int fd, MappedBuffer[] buffers, int width, int height, double fps, ILogger logger)
        {
            _fd = fd;
            _buffers = buffers;
            Width = width;
            Height = height;
            DeviceFps = fps;
            _logger = logger;
        }

        public int Width { get; }
        public int Height { get; }
        public double DeviceFps { get; private set; }

        public static LinuxV4l2MjpegSession? TryOpen(
            int index, int targetFps, int maxWidth, ILogger logger, out bool noUsableMjpegPin)
        {
            noUsableMjpegPin = false;
            var pins = LinuxV4l2Devices.TryQueryPins(index);
            var pick = VideoCapturePinRank.PickMjpegCaptureMeetingFps(pins, targetFps, maxWidth);
            if (pick is null)
            {
                noUsableMjpegPin = !pins.Any(p => p.Jpeg);
                logger.LogWarning("Radio Display V4L2: no MJPEG pin for {Fps} fps ({PinCount} pins)", targetFps, pins.Count);
                return null;
            }

            var fd = LinuxV4l2Devices.OpenCaptureFd(index);
            if (fd < 0)
            {
                logger.LogWarning("Radio Display V4L2: could not open {Path} RDWR", LinuxV4l2Devices.DevicePath(index));
                return null;
            }

            MappedBuffer[]? mapped = null;
            try
            {
                if (!LinuxV4l2Devices.TryConfigureMjpeg(fd, pick.Value.Width, pick.Value.Height, targetFps)
                    && !(targetFps >= 30 && LinuxV4l2Devices.TryConfigureMjpeg(fd, 640, 480, targetFps)))
                {
                    logger.LogWarning(
                        "Radio Display V4L2: S_FMT MJPEG {W}x{H}@{Fps} failed",
                        pick.Value.Width, pick.Value.Height, targetFps);
                    noUsableMjpegPin = false;
                    LinuxV4l2Devices.CloseFd(fd);
                    return null;
                }

                mapped = MapBuffers(fd);
                if (mapped is null)
                {
                    logger.LogWarning("Radio Display V4L2: REQBUFS/mmap failed");
                    LinuxV4l2Devices.CloseFd(fd);
                    return null;
                }

                var type = (int)BufTypeCapture;
                if (Ioctl(fd, VidIocStreamon, ref type) != 0)
                {
                    logger.LogWarning("Radio Display V4L2: STREAMON failed errno={Errno}", Marshal.GetLastPInvokeError());
                    Unmap(mapped);
                    LinuxV4l2Devices.CloseFd(fd);
                    return null;
                }

                logger.LogInformation(
                    "Radio Display V4L2 MJPEG {W}x{H} target {Fps} fps (native mmap, not OpenCV)",
                    pick.Value.Width, pick.Value.Height, targetFps);

                var session = new LinuxV4l2MjpegSession(fd, mapped, pick.Value.Width, pick.Value.Height, targetFps, logger);
                session._streaming = true;
                return session;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Radio Display V4L2 MJPEG open failed");
                if (mapped is not null)
                    Unmap(mapped);
                LinuxV4l2Devices.CloseFd(fd);
                return null;
            }
        }

        public bool TrySetFrameRate(int targetFps)
        {
            if (_disposed || targetFps < 1)
                return false;
            if (LinuxV4l2Devices.TrySetFrameRateFd(_fd, targetFps))
            {
                DeviceFps = targetFps;
                return true;
            }

            return false;
        }

        public bool TryReadJpeg(out byte[]? jpeg)
        {
            jpeg = null;
            if (_disposed || !_streaming)
                return false;

            var buf = new V4l2Buffer
            {
                Type = BufTypeCapture,
                Memory = MemoryMmap
            };
            if (Ioctl(_fd, VidIocDqbuf, ref buf) != 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno != Eagain)
                    _logger.LogDebug("Radio Display V4L2 DQBUF errno={Errno}", errno);
                return false;
            }

            try
            {
                if (buf.Index >= (uint)_buffers.Length || buf.BytesUsed < 4)
                    return false;

                var map = _buffers[buf.Index];
                var len = (int)Math.Min(buf.BytesUsed, map.Length);
                if (map.Start == IntPtr.Zero || len < 4)
                    return false;
                if (Marshal.ReadByte(map.Start) != 0xFF || Marshal.ReadByte(map.Start, 1) != 0xD8)
                    return false;

                var bytes = new byte[len];
                Marshal.Copy(map.Start, bytes, 0, len);
                jpeg = TrimJpeg(bytes);
                return jpeg is { Length: > 0 };
            }
            finally
            {
                buf.Type = BufTypeCapture;
                buf.Memory = MemoryMmap;
                _ = Ioctl(_fd, VidIocQbuf, ref buf);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_streaming)
            {
                var type = (int)BufTypeCapture;
                try { _ = Ioctl(_fd, VidIocStreamoff, ref type); } catch { /* ignore */ }
                _streaming = false;
            }

            Unmap(_buffers);
            LinuxV4l2Devices.CloseFd(_fd);
        }

        private static MappedBuffer[]? MapBuffers(int fd)
        {
            var req = new V4l2RequestBuffers
            {
                Count = BufferCount,
                Type = BufTypeCapture,
                Memory = MemoryMmap
            };
            if (Ioctl(fd, VidIocReqbufs, ref req) != 0 || req.Count < 2)
                return null;

            var mapped = new MappedBuffer[req.Count];
            for (uint i = 0; i < req.Count; i++)
            {
                var buf = new V4l2Buffer
                {
                    Index = i,
                    Type = BufTypeCapture,
                    Memory = MemoryMmap
                };
                if (Ioctl(fd, VidIocQuerybuf, ref buf) != 0)
                {
                    Unmap(mapped);
                    return null;
                }

                var ptr = mmap(IntPtr.Zero, (nuint)buf.Length, ProtRead | ProtWrite, MapShared, fd, buf.Offset);
                if (ptr == MapFailed || ptr == IntPtr.Zero)
                {
                    Unmap(mapped);
                    return null;
                }

                mapped[i] = new MappedBuffer(ptr, buf.Length);
                buf.Type = BufTypeCapture;
                buf.Memory = MemoryMmap;
                buf.Index = i;
                if (Ioctl(fd, VidIocQbuf, ref buf) != 0)
                {
                    Unmap(mapped);
                    return null;
                }
            }

            return mapped;
        }

        private static void Unmap(MappedBuffer[] buffers)
        {
            foreach (var b in buffers)
            {
                if (b.Start == IntPtr.Zero || b.Start == MapFailed)
                    continue;
                try { _ = munmap(b.Start, (nuint)b.Length); } catch { /* ignore */ }
            }
        }

        private static byte[] TrimJpeg(byte[] bytes)
        {
            for (var i = bytes.Length - 2; i >= 2; i--)
            {
                if (bytes[i] == 0xFF && bytes[i + 1] == 0xD9)
                    return bytes.AsSpan(0, i + 2).ToArray();
            }

            return bytes;
        }

        private readonly record struct MappedBuffer(IntPtr Start, uint Length);

        [StructLayout(LayoutKind.Sequential, Size = 20)]
        private struct V4l2RequestBuffers
        {
            public uint Count;
            public uint Type;
            public uint Memory;
            public uint Capabilities;
            public byte Flags;
            public byte R0, R1, R2;
        }

        [StructLayout(LayoutKind.Sequential, Size = 88)]
        private struct V4l2Buffer
        {
            public uint Index;
            public uint Type;
            public uint BytesUsed;
            public uint Flags;
            public uint Field;
            public uint TimestampPad;
            public long TimestampSec;
            public long TimestampUsec;
            public uint TcType;
            public uint TcFlags;
            public uint TcRest;
            public uint TcUser;
            public uint Sequence;
            public uint Memory;
            public uint Offset;
            public uint OffsetPad;
            public uint Length;
            public uint Reserved2;
            public int RequestFd;
        }

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int Ioctl(int fd, UIntPtr request, ref V4l2RequestBuffers arg);

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int Ioctl(int fd, UIntPtr request, ref V4l2Buffer arg);

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int Ioctl(int fd, UIntPtr request, ref int arg);

        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr mmap(IntPtr addr, nuint length, int prot, int flags, int fd, long offset);

        [DllImport("libc", SetLastError = true)]
        private static extern int munmap(IntPtr addr, nuint length);
    }
}
