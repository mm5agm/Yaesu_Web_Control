using Avalonia.Threading;
using OpenCvSharp;

namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// OpenCV's AVFoundation backend delivers sample buffers on a GCD queue, but
    /// opening/reading from a plain worker thread still stalls after the first
    /// frame. Keep Open + Read + Release on the Avalonia/AppKit UI thread.
    /// Never Release from another thread while Read is in <c>objc_msgSend</c>
    /// — that SIGSEGVs (CaptureDelegate use-after-free).
    /// </summary>
    internal static class MacAvFoundationCapture
    {
        private static readonly object OpLock = new();

        public static T OnUiThread<T>(Func<T> work)
        {
            if (Dispatcher.UIThread.CheckAccess())
                return work();

            T? result = default;
            Exception? error = null;
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                try { result = work(); }
                catch (Exception ex) { error = ex; }
            }).GetAwaiter().GetResult();

            if (error is not null)
                throw error;
            return result!;
        }

        public static void OnUiThread(Action work)
        {
            OnUiThread(() =>
            {
                work();
                return 0;
            });
        }

        public static VideoCapture? OpenOnUiThread(Func<VideoCapture?> open) =>
            OnUiThread(() =>
            {
                lock (OpLock)
                    return open();
            });

        public static bool ReadOnUiThread(VideoCapture cap, Mat frame) =>
            OnUiThread(() =>
            {
                lock (OpLock)
                {
                    try
                    {
                        if (!cap.IsOpened())
                            return false;

                        using var tmp = new Mat();
                        if (!cap.Read(tmp) || tmp.Empty())
                            return false;
                        tmp.CopyTo(frame);
                        return !frame.Empty();
                    }
                    catch (ObjectDisposedException)
                    {
                        return false;
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                }
            });

        public static void ReleaseOnUiThread(VideoCapture? cap)
        {
            if (cap is null)
                return;
            OnUiThread(() =>
            {
                lock (OpLock)
                    DisposeQuietly(cap);
            });
        }

        /// <summary>
        /// Last-resort teardown when a UI-thread Read will not return. Waits
        /// briefly for an in-flight Read (OpLock) so we do not free
        /// CaptureDelegate under <c>objc_msgSend</c>. If Read is still stuck,
        /// Release from this thread anyway — that can crash, but the alternative
        /// is a permanently wedged camera session.
        /// </summary>
        public static void ForceUnblockAndRelease(VideoCapture? cap)
        {
            if (cap is null)
                return;

            if (Monitor.TryEnter(OpLock, TimeSpan.FromMilliseconds(1500)))
            {
                try { DisposeQuietly(cap); }
                finally { Monitor.Exit(OpLock); }
                return;
            }

            DisposeQuietly(cap);
        }

        private static void DisposeQuietly(VideoCapture cap)
        {
            try { cap.Release(); } catch { /* ignore */ }
            try { cap.Dispose(); } catch { /* ignore */ }
        }
    }
}
