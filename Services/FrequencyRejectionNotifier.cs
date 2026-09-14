using Microsoft.AspNetCore.SignalR;
using Yaesu_Web_Control.Hubs;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Tells the browser when an external program asked for a frequency this
    /// radio cannot tune, so the request was refused.
    ///
    /// Why this exists. Two paths reject out-of-range frequencies —
    /// <see cref="RigctldServer"/> (returns <c>RPRT -1</c>) and
    /// <see cref="WsjtxUdpService"/> (ignores the broadcast) — and until
    /// 2026-08-17 both were completely silent to the operator: one Debug log
    /// line each, in a log that is off by default. What the operator actually
    /// saw was WSJT-X throwing "Rig control error" with nothing in YWC to
    /// explain it, or, on the UDP path, nothing at all.
    ///
    /// This is deliberately NOT an attempt to stop WSJT-X erroring. WSJT-X
    /// decides that from the rigctld TCP reply and never sees the browser, so
    /// no UI change can prevent it. The banner explains the error; it does not
    /// suppress it. (Suppressing it would mean returning RPRT 0 for something
    /// we did not do, which lies to every rigctld client — Log4OM, N1MM,
    /// FLDigi — not just to WSJT-X.)
    ///
    /// Throttled because WSJT-X rebroadcasts its UDP status roughly once a
    /// second while it sits on the offending band. Without the throttle the
    /// banner would re-fire ~60 times a minute and read as a stuck overlay
    /// rather than an event.
    /// </summary>
    public class FrequencyRejectionNotifier
    {
        private readonly IHubContext<RadioHub> _hubContext;
        private readonly ILogger<FrequencyRejectionNotifier> _logger;

        private readonly object _lock = new();
        private long _lastFrequencyHz;
        private DateTime _lastSentUtc = DateTime.MinValue;

        /// <summary>
        /// How long the same rejected frequency stays quiet after being
        /// announced once. Comfortably longer than the banner's own dwell
        /// time so it cannot retrigger while still on screen.
        /// </summary>
        private static readonly TimeSpan RepeatInterval = TimeSpan.FromSeconds(30);

        public FrequencyRejectionNotifier(
            IHubContext<RadioHub> hubContext,
            ILogger<FrequencyRejectionNotifier> logger)
        {
            _hubContext = hubContext;
            _logger = logger;
        }

        /// <summary>
        /// Announce a refused frequency. Safe to call on every repeat of the
        /// same request — repeats inside <see cref="RepeatInterval"/> are
        /// dropped. A <em>different</em> frequency always announces
        /// immediately, so stepping through several unsupported bands is not
        /// silently swallowed.
        /// </summary>
        /// <param name="frequencyHz">The frequency that was refused.</param>
        /// <param name="source">Who asked — shown to the operator, e.g. "WSJT-X".</param>
        public async Task NotifyAsync(long frequencyHz, string source)
        {
            lock (_lock)
            {
                if (frequencyHz == _lastFrequencyHz
                    && DateTime.UtcNow - _lastSentUtc < RepeatInterval)
                {
                    return;
                }
                _lastFrequencyHz = frequencyHz;
                _lastSentUtc = DateTime.UtcNow;
            }

            var mhz = frequencyHz / 1_000_000.0;
            _logger.LogInformation(
                "[FrequencyRejection] {Source} asked for {Mhz:F3} MHz, which this radio cannot tune — refused",
                source, mhz);

            try
            {
                await _hubContext.Clients.All.SendAsync("FrequencyRejected", new
                {
                    frequencyHz,
                    frequencyMhz = mhz,
                    source
                });
            }
            catch (Exception ex)
            {
                // A browser that is not listening must never break rig control.
                _logger.LogDebug(ex, "[FrequencyRejection] Could not broadcast to browsers");
            }
        }
    }
}
