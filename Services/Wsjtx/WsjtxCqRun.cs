namespace Yaesu_Web_Control.Services.Wsjtx
{
    /// <summary>
    /// Counts the CQs WSJT-Z sends after the FT8 Tiles Call CQ tile, so the
    /// run can stop by itself after a set number nobody answered (Mike
    /// 2E0UMF, 2026-11-28: "I may fall asleep").
    ///
    /// Fed from every WSJT-X Status. A CQ is a transmission whose Tx Message
    /// starts "CQ", counted once when it starts. A transmission that isn't a
    /// CQ means a station answered and WSJT-Z is working it: the count goes
    /// back to 0, so the limit is CQs in a row with no answer, and a QSO in
    /// progress is never cut off. When the last allowed CQ has finished,
    /// <see cref="Update"/> returns true once, and the caller turns Auto CQ
    /// and Enable Tx off.
    /// </summary>
    public sealed class WsjtxCqRun
    {
        private readonly object _lock = new();
        private bool _active;
        private int _limit;
        private int _sent;
        private bool _wasTransmitting;
        private bool _currentIsCq;

        /// <summary>True from Call CQ until the run stops or is cancelled.</summary>
        public bool Active { get { lock (_lock) return _active; } }

        /// <summary>The most CQs in a row with no answer; 0 = no limit.</summary>
        public int Limit { get { lock (_lock) return _limit; } }

        /// <summary>CQs sent in a row since Call CQ or the last answer.</summary>
        public int Sent { get { lock (_lock) return _sent; } }

        /// <summary>Starts a run. limit 0 or less means call until stopped.</summary>
        public void Start(int limit, bool transmittingNow)
        {
            lock (_lock)
            {
                _active = true;
                _limit = Math.Max(0, limit);
                _sent = 0;
                _wasTransmitting = transmittingNow;
                _currentIsCq = false;
            }
        }

        public void Cancel()
        {
            lock (_lock) _active = false;
        }

        public static bool IsCq(string? txMessage) =>
            !string.IsNullOrWhiteSpace(txMessage)
            && (txMessage.TrimStart().StartsWith("CQ ", StringComparison.OrdinalIgnoreCase)
                || txMessage.Trim().Equals("CQ", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Feeds one Status. Returns true once, when the limit has been reached
        /// and the last CQ has finished: time to stop.
        /// </summary>
        public bool Update(bool transmitting, string? txMessage)
        {
            lock (_lock)
            {
                if (!_active) { _wasTransmitting = transmitting; return false; }

                bool started = transmitting && !_wasTransmitting;
                bool ended = !transmitting && _wasTransmitting;
                _wasTransmitting = transmitting;

                if (started)
                {
                    _currentIsCq = IsCq(txMessage);
                    if (_currentIsCq) _sent++;
                    else _sent = 0;   // answering someone: a fresh count after the QSO
                }

                if (ended && _currentIsCq && _limit > 0 && _sent >= _limit)
                {
                    _active = false;
                    return true;
                }
                return false;
            }
        }
    }
}
