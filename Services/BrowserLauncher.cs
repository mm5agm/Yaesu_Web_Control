// BrowserLauncher.cs
using System.Diagnostics;

namespace Yaesu_Web_Control.Services
{
    public class BrowserLauncher
    {
        private bool _opened = false;
        private readonly object _lock = new();

        public void OpenOnce(string url)
        {
            // Headless / Docker hosts have no local browser to open.
            if (HostRuntime.IsContainer) return;

            lock (_lock)
            {
                if (_opened) return;
                _opened = true;
            }
            // Small delay ensures Kestrel is fully accepting connections before the
            // browser navigates — avoids a blank tab on first launch after install.
            Task.Run(async () =>
            {
                await Task.Delay(600);
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                catch { }
            });
        }
    }
}