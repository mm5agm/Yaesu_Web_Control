namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Set by Settings' Restart Now just before it stops the host, so shutdown
    /// work that only belongs to a real close (switching the radio off) is
    /// skipped while YWC relaunches.
    /// </summary>
    public static class AppLifecycle
    {
        public static volatile bool RestartRequested;
    }
}
