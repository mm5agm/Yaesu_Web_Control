using System;

namespace RadioWebControl.Core.Services.TxAlert
{
    /// <summary>
    /// The operator's limits for the transmit alert (YWC Discussion #257).
    /// SWR is a ratio (2.5 means 2.5 : 1). ALC is a percentage of the meter's
    /// full scale (raw / 255), the same in both apps, and is only watched in
    /// the audio data modes, where any real ALC action distorts the signal.
    /// </summary>
    public sealed record TxAlertLimits(bool Enabled, double SwrLimit, bool AlcEnabled, double AlcPercentLimit)
    {
        public const double DefaultSwr = 2.5;
        public const double MinSwr = 1.5;
        public const double MaxSwr = 5.0;
        public const double DefaultAlcPercent = 50;
        public const double MinAlcPercent = 10;
        public const double MaxAlcPercent = 100;

        public static double ClampSwr(double v) =>
            double.IsFinite(v) ? Math.Round(Math.Clamp(v, MinSwr, MaxSwr), 1) : DefaultSwr;

        public static double ClampAlcPercent(double v) =>
            double.IsFinite(v) ? Math.Round(Math.Clamp(v, MinAlcPercent, MaxAlcPercent)) : DefaultAlcPercent;
    }

    /// <summary>
    /// One look at the meters. <paramref name="Swr"/> is the calibrated ratio
    /// and <paramref name="AlcPercent"/> is 0-100 of the meter's scale; either
    /// is null when the app has no reading. <paramref name="Mode"/> is the
    /// transmit VFO's mode name as the app shows it (DATA-U, PSK, RTTY-L...).
    /// </summary>
    public sealed record TxAlertReading(bool Transmitting, double? Swr, double? AlcPercent, string? Mode);

    /// <summary>
    /// One alert. <see cref="Active"/> while the reading is still over the
    /// limit; afterwards it stays as a record of the last over, with its
    /// peak, until <see cref="TxAlertMonitor.KeepEndedMs"/> has passed or a
    /// new alert replaces it. <see cref="Id"/> is new for each alert, so a
    /// page that dismissed one still shows the next.
    /// </summary>
    public sealed record TxAlertEvent(long Id, bool Active, double Peak, double Limit, DateTimeOffset At, string? Mode);

    public sealed record TxAlertSnapshot(bool Enabled, TxAlertEvent? Swr, TxAlertEvent? Alc);

    /// <summary>
    /// Decides when the SWR or ALC is high enough, for long enough, to put an
    /// alert in front of the operator. No radio, settings or hub in here: the
    /// app hands it a calibrated reading and its limits, as often as it likes
    /// (every 100 ms is right), and shows what <see cref="Update"/> returns.
    ///
    /// A reading has to stay over the limit for <see cref="HoldMs"/> before it
    /// counts, because both apps poll the meters about every 200 ms and SWR
    /// can jump for one poll as the radio keys up. Within one over, an alert
    /// that drops below the limit and comes back is the same alert, so a
    /// reading that sits on the limit doesn't throw up a new pop-up every
    /// second after the operator has dismissed it.
    /// </summary>
    public sealed class TxAlertMonitor
    {
        public const int HoldMs = 400;
        public const int KeepEndedMs = 5 * 60 * 1000;

        private readonly Func<long> _nowMs;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly object _lock = new();
        private readonly Tracker _swr = new();
        private readonly Tracker _alc = new();
        private long _nextId;
        private long _over;
        private bool _wasTransmitting;
        private TxAlertSnapshot _last = new(false, null, null);

        public TxAlertMonitor(Func<long>? nowMs = null, Func<DateTimeOffset>? utcNow = null)
        {
            _nowMs = nowMs ?? (() => Environment.TickCount64);
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>The last result of <see cref="Update"/>.</summary>
        public TxAlertSnapshot Snapshot
        {
            get { lock (_lock) return _last; }
        }

        /// <summary>
        /// FT8, PSK and the other modes that send audio into the radio, where
        /// ALC is the sign of too much drive. Not RTTY/FSK (keyed, no audio),
        /// and not SSB, where some ALC is normal.
        /// </summary>
        public static bool IsAudioDataMode(string? mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return false;
            var m = mode.Trim().ToUpperInvariant();
            return m.StartsWith("DATA", StringComparison.Ordinal)
                || m.StartsWith("PKT", StringComparison.Ordinal)
                || m.StartsWith("PSK", StringComparison.Ordinal)
                || m.EndsWith("-D", StringComparison.Ordinal);
        }

        /// <summary>Take one reading and return what to show.</summary>
        public TxAlertSnapshot Update(TxAlertReading reading, TxAlertLimits limits)
        {
            lock (_lock)
            {
                long now = _nowMs();
                if (reading.Transmitting && !_wasTransmitting) _over++;
                _wasTransmitting = reading.Transmitting;

                if (!limits.Enabled)
                {
                    _swr.Clear();
                    _alc.Clear();
                    return _last = new TxAlertSnapshot(false, null, null);
                }

                double swrLimit = TxAlertLimits.ClampSwr(limits.SwrLimit);
                double alcLimit = TxAlertLimits.ClampAlcPercent(limits.AlcPercentLimit);
                bool alcWatched = limits.AlcEnabled && IsAudioDataMode(reading.Mode);

                Step(_swr, reading.Transmitting ? reading.Swr : null, swrLimit, reading.Mode, now);
                if (alcWatched)
                    Step(_alc, reading.Transmitting ? reading.AlcPercent : null, alcLimit, reading.Mode, now);
                else if (!limits.AlcEnabled)
                    _alc.Clear();
                else
                    Step(_alc, null, alcLimit, reading.Mode, now);

                return _last = new TxAlertSnapshot(true, _swr.Shown(now), _alc.Shown(now));
            }
        }

        private void Step(Tracker t, double? value, double limit, string? mode, long now)
        {
            bool over = value is double v && v > limit;
            if (!over)
            {
                t.OverSince = null;
                if (t.Event is { Active: true } e)
                {
                    t.Event = e with { Active = false };
                    t.EndedMs = now;
                }
                return;
            }

            double val = value!.Value;
            t.OverSince ??= now;
            if (t.Event is { Active: true } live)
            {
                if (val > live.Peak) t.Event = live with { Peak = val };
                return;
            }
            if (now - t.OverSince.Value < HoldMs) return;

            if (t.Event is { } last && t.EventOver == _over)
                t.Event = last with { Active = true, Peak = Math.Max(last.Peak, val), Limit = limit };
            else
            {
                t.Event = new TxAlertEvent(++_nextId, true, val, limit, _utcNow(), mode);
                t.EventOver = _over;
            }
            t.EndedMs = null;
        }

        private sealed class Tracker
        {
            public long? OverSince;
            public TxAlertEvent? Event;
            public long EventOver;
            public long? EndedMs;

            public TxAlertEvent? Shown(long now)
            {
                if (Event is { Active: false } && EndedMs is long ended && now - ended >= KeepEndedMs)
                {
                    Event = null;
                    EndedMs = null;
                }
                return Event;
            }

            public void Clear()
            {
                OverSince = null;
                Event = null;
                EndedMs = null;
            }
        }
    }
}
