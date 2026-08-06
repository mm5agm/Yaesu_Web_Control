// BrowserLauncher.cs
using System.Diagnostics;

namespace Yaesu_Web_Control.Services
{
    public class BrowserLauncher
    {
        private bool _opened = false;
        private readonly object _lock = new();
        private readonly ISettingsService _settings;

        public BrowserLauncher(ISettingsService settings)
        {
            _settings = settings;
        }

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
                try
                {
                    var settings = await _settings.GetSettingsAsync();
                    if (!settings.OpenBrowserOnStartup) return;
                }
                catch
                {
                    // Settings read failed — keep the historical default (open).
                }

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
