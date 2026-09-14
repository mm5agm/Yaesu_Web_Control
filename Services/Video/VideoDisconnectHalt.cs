namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// Session-scoped halt after a capture disconnect. Persists until the
    /// operator explicitly starts capture or changes the device key.
    /// Not written to disk — a process restart is a fresh session.
    /// </summary>
    public sealed class VideoDisconnectHalt
    {
        private int _halted;

        /// <summary>True while capture must not reopen until operator recovery.</summary>
        public bool IsActive => Volatile.Read(ref _halted) != 0;

        /// <summary>Engage the halt (e.g. unplug, stall watchdog, open failure).</summary>
        public void Set() => Interlocked.Exchange(ref _halted, 1);

        /// <summary>Clear the halt (operator Start or intentional device change).</summary>
        public void Clear() => Interlocked.Exchange(ref _halted, 0);
    }
}
