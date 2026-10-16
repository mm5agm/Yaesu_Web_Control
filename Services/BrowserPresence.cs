namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Whether any browser is connected to the hub, for services that want to
    /// stop work nobody is watching. RadioHub reports every change; it owns
    /// the connection count, this only passes the answer on, so a service
    /// such as SdrManager needs no reference to the hub.
    /// </summary>
    public sealed class BrowserPresence
    {
        private readonly object _lock = new();
        private bool _present;

        /// <summary>True while at least one browser connection is open.</summary>
        public bool AnyPresent { get { lock (_lock) return _present; } }

        /// <summary>Raised with the new value whenever presence changes.</summary>
        public event Action<bool>? Changed;

        public void Report(bool present)
        {
            lock (_lock)
            {
                if (_present == present) return;
                _present = present;
            }
            Changed?.Invoke(present);
        }
    }
}
