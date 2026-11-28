namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// The highest ALC and SWR in each transmission, for the FT8 Tiles status
    /// line (Mike 2E0UMF, 2026-11-28: a way to check his TX audio level from
    /// another room). Fed every TxAlertService tick. The first second of each
    /// transmission is skipped, while the meters settle.
    /// </summary>
    public sealed class TxPeakTracker
    {
        public static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);

        public sealed record Peaks(double AlcPercent, double Swr, DateTime StartedUtc, DateTime? EndedUtc);

        private readonly object _lock = new();
        private DateTime? _start;
        private double _alc;
        private double _swr;
        private bool _any;
        private Peaks? _last;

        /// <summary>The transmission going on now, once past the settle time; else null.</summary>
        public Peaks? Current { get { lock (_lock) return _start is { } s && _any ? new Peaks(_alc, _swr, s, null) : null; } }

        /// <summary>The last finished transmission that lasted past the settle time; null if none yet.</summary>
        public Peaks? Last { get { lock (_lock) return _last; } }

        public void Update(bool transmitting, double alcPercent, double swr, DateTime utc)
        {
            lock (_lock)
            {
                if (transmitting)
                {
                    if (_start is null) { _start = utc; _alc = 0; _swr = 0; _any = false; }
                    if (utc - _start.Value < Settle) return;
                    _alc = _any ? Math.Max(_alc, alcPercent) : alcPercent;
                    _swr = _any ? Math.Max(_swr, swr) : swr;
                    _any = true;
                }
                else if (_start is { } s)
                {
                    if (_any) _last = new Peaks(_alc, _swr, s, utc);
                    _start = null;
                }
            }
        }
    }
}
