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
            get { lock (_lock) return _socket is { State: WebSocketState.Open }; }
        }

        public string? ActiveConnectionId
        {
            get { lock (_lock) return _connectionId; }
        }

        /// <summary>
        /// Try to claim the single audio session. Returns false if busy.
        /// </summary>
        public bool TryAcquire(string connectionId, WebSocket socket)
        {
            lock (_lock)
            {
                if (_socket is { State: WebSocketState.Open })
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
