using System.Net.WebSockets;

namespace Yaesu_Web_Control.Services.Audio
{
    /// <summary>
    /// Single active remote-audio session. A second connect is rejected busy.
    /// </summary>
    public sealed class AudioSessionManager
    {
        private readonly object _lock = new();
        private string? _connectionId;
        private WebSocket? _socket;

        public bool HasActiveSession
        {
            get { lock (_lock) return _connectionId != null; }
        }

        public string? ActiveConnectionId
        {
            get { lock (_lock) return _connectionId; }
        }

        /// <summary>
        /// Try to claim the single audio session. Returns false if busy.
        /// Busy until <see cref="Release"/> — not merely while the socket is
        /// Open — so a reconnect cannot start while StopSessionAsync is still
        /// tearing down PortAudio streams.
        /// </summary>
        public bool TryAcquire(string connectionId, WebSocket socket)
        {
            lock (_lock)
            {
                if (_connectionId != null)
                    return false;
                _connectionId = connectionId;
                _socket = socket;
                return true;
            }
        }

        public void Release(string connectionId)
        {
            lock (_lock)
            {
                if (_connectionId == connectionId)
                {
                    _connectionId = null;
                    _socket = null;
                }
            }
        }

        public WebSocket? CurrentSocket
        {
            get { lock (_lock) return _socket; }
        }
    }
}
