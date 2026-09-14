namespace Yaesu_Web_Control.Services.Video
{
    /// <summary>
    /// Ref-counts MJPEG viewers. Capture starts on first acquire and stops
    /// when the last viewer releases — idle hosts pay zero capture CPU.
    /// </summary>
    public sealed class VideoSessionManager
    {
        private readonly object _lock = new();
        private int _viewers;
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

        public int ViewerCount
        {
            get { lock (_lock) return _viewers; }
        }

        /// <summary>
        /// Register a viewer. Returns true if this was the first viewer
        /// (caller should start capture).
        /// </summary>
        public bool TryAcquire(string viewerId, out int viewerCount)
        {
            lock (_lock)
            {
                if (!_ids.Add(viewerId))
                {
                    viewerCount = _viewers;
                    return false;
                }

                _viewers++;
                viewerCount = _viewers;
                return _viewers == 1;
            }
        }

        /// <summary>
        /// Unregister a viewer. Returns true if this was the last viewer
        /// (caller should stop capture).
        /// </summary>
        public bool Release(string viewerId, out int viewerCount)
        {
            lock (_lock)
            {
                if (!_ids.Remove(viewerId))
                {
                    viewerCount = _viewers;
                    return false;
                }

                _viewers = Math.Max(0, _viewers - 1);
                viewerCount = _viewers;
                return _viewers == 0;
            }
        }
    }
}
