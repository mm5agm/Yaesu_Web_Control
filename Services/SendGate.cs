using System.Threading;

namespace Yaesu_Web_Control.Services;

/// <summary>
/// One frame send at a time, for the pumps that push spectrum frames to the
/// pages: a new frame is dropped while one is in flight, rather than queued
/// behind a slow page. A send that never finishes (or throws before it gets
/// going) used to hold a plain flag for good and the page waited forever, so a
/// send older than <see cref="StuckMs"/> is given up on and the next frame goes.
/// Same rule as AudioWaterfallService's guard (v2.5.6-pre7).
/// </summary>
public sealed class SendGate
{
    public const long StuckMs = 2000;

    private readonly Func<long> _clock;
    private long _startedMs;   // clock value when the send in flight began; 0 = none

    public SendGate(Func<long>? clock = null) => _clock = clock ?? (() => Environment.TickCount64);

    /// <summary>
    /// True when this frame may be sent: <paramref name="stuck"/> says the one
    /// before it was given up on. Pass <paramref name="token"/> to <see cref="End"/>.
    /// </summary>
    public bool TryBegin(out long token, out bool stuck)
    {
        stuck = false;
        long now = Math.Max(1, _clock());
        token = now;
        long started = Interlocked.Read(ref _startedMs);
        if (started != 0)
        {
            if (now - started < StuckMs) return false;
            if (Interlocked.CompareExchange(ref _startedMs, now, started) != started) return false;
            stuck = true;
            return true;
        }
        return Interlocked.CompareExchange(ref _startedMs, now, 0) == 0;
    }

    /// <summary>The send begun with <paramref name="token"/> has finished, or failed.</summary>
    public void End(long token) => Interlocked.CompareExchange(ref _startedMs, 0, token);
}
