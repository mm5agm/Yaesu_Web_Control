using System;
using System.Collections.Generic;
using System.Linq;

namespace Yaesu_Web_Control.Services.Audio
{
    /// <summary>One signal the guesser found in the audio, and what it looks like.</summary>
    /// <param name="LowHz">Where the signal starts, audio Hz.</param>
    /// <param name="HighHz">Where it ends.</param>
    /// <param name="Mode">Short name: FT8, CW, RTTY, Piccolo, Olivia 8/250, Unknown...</param>
    /// <param name="Detail">What the guess rests on, in plain words.</param>
    /// <param name="FromWsjtx">True when WSJT-X decoded this mode here, so it's not a guess.</param>
    public sealed record AudioModeGuess(double LowHz, double HighHz, string Mode, string Detail, bool FromWsjtx);

    /// <summary>A recent WSJT-X decode: its audio offset (DF) and mode name.</summary>
    public readonly record struct AudioDecodeHint(double OffsetHz, string Mode);

    /// <summary>
    /// Guesses the mode of each signal in the radio's audio from the shape it
    /// makes on the waterfall: how wide it is, whether it is a comb of tones and
    /// how far apart they are, whether it keys on and off, and whether it comes
    /// in bursts on the FT8 or FT4 clock.
    ///
    /// It looks only at the spectrum, at 5.86 Hz a bin and a frame every 43 ms,
    /// so a symbol shorter than the 171 ms block blurs: an MFSK mode shows all
    /// its tones at once as a comb, and RTTY shows both tones. That's what the
    /// rules use. The answer is a guess and the page says so, except where
    /// WSJT-X has decoded something at that audio offset.
    ///
    /// Thread-safe: the audio thread calls <see cref="Add"/>, a timer calls
    /// <see cref="Classify"/>.
    /// </summary>
    public sealed class AudioModeClassifier
    {
        // How far above the noise a bin must be to count as signal in a frame.
        // Noise power in one bin is exponential, so 7 dB above its mean comes
        // up by chance about 0.7% of the time.
        internal const double OnDb = 7.0;
        private const double OffDb = 3.0;
        private const double ShortSeconds = 4.0;
        private const double KeyingSeconds = 8.0;
        private const double MinHz = 100.0;

        // RTTY shifts the guesser knows: 170 Hz is the amateur one.
        private static readonly double[] RttyShifts = { 85, 170, 200, 425, 450, 850 };

        // Tone spacings of the multi-tone modes the guesser can name: MFSK16,
        // Piccolo, and Olivia/Contestia's two.
        private static readonly double[] KnownSpacings = { 15.625, 20, 31.25, 62.5 };

        public double BinHz { get; }
        public double FrameSeconds { get; }
        public int BinCount { get; }

        private readonly object _lock = new();
        private readonly float[][] _frames;
        private readonly DateTime[] _times;
        private int _head;
        private int _count;

        public AudioModeClassifier(double binHz, double frameSeconds, int binCount, double historySeconds = 32)
        {
            BinHz = binHz;
            FrameSeconds = frameSeconds;
            BinCount = binCount;
            int capacity = Math.Max(8, (int)Math.Ceiling(historySeconds / frameSeconds));
            _frames = new float[capacity][];
            for (int i = 0; i < capacity; i++) _frames[i] = new float[binCount];
            _times = new DateTime[capacity];
        }

        /// <summary>Adds one frame of levels in dB, taken at utc.</summary>
        public void Add(ReadOnlySpan<float> levelsDb, DateTime utc)
        {
            lock (_lock)
            {
                levelsDb.Slice(0, Math.Min(levelsDb.Length, BinCount)).CopyTo(_frames[_head]);
                _times[_head] = utc;
                _head = (_head + 1) % _frames.Length;
                if (_count < _frames.Length) _count++;
            }
        }

        public void Clear()
        {
            lock (_lock) { _head = 0; _count = 0; }
        }

        /// <summary>The signals heard over the last half minute, lowest first.</summary>
        public IReadOnlyList<AudioModeGuess> Classify(IReadOnlyList<AudioDecodeHint>? hints = null)
        {
            float[][] frames;
            DateTime[] times;
            lock (_lock)
            {
                if (_count * FrameSeconds < 2.0) return Array.Empty<AudioModeGuess>();
                frames = new float[_count][];
                times = new DateTime[_count];
                int start = (_head - _count + _frames.Length) % _frames.Length;
                for (int i = 0; i < _count; i++)
                {
                    int j = (start + i) % _frames.Length;
                    frames[i] = (float[])_frames[j].Clone();
                    times[i] = _times[j];
                }
            }
            return new Analysis(this, frames, times).Run(hints ?? Array.Empty<AudioDecodeHint>());
        }

        // ── one pass over the history ─────────────────────────────────────────

        private sealed class Analysis
        {
            private readonly AudioModeClassifier _c;
            private readonly float[][] _f;
            private readonly DateTime[] _t;
            private readonly int _n;
            private readonly int _bins;
            private readonly int _lo;
            private readonly int _hi;
            private readonly double[] _floor;
            private readonly double[] _meanDb;   // time-average power over the floor, dB
            private readonly double[] _shortDb;  // the same over the last few seconds
            private readonly double[] _active;
            private readonly int _shortFrom;

            public Analysis(AudioModeClassifier c, float[][] frames, DateTime[] times)
            {
                _c = c;
                _f = frames;
                _t = times;
                _n = frames.Length;
                _bins = c.BinCount;
                _lo = Math.Max(1, (int)Math.Ceiling(MinHz / c.BinHz));
                _hi = _bins - 2;
                _shortFrom = Math.Max(0, _n - (int)Math.Round(ShortSeconds / c.FrameSeconds));
                _floor = NoiseFloor();
                _meanDb = new double[_bins];
                _shortDb = new double[_bins];
                _active = new double[_bins];
                for (int k = _lo; k <= _hi; k++)
                {
                    double sum = 0, sumShort = 0;
                    int on = 0;
                    for (int t = 0; t < _n; t++)
                    {
                        double e = _f[t][k] - _floor[k];
                        double p = Math.Pow(10, e / 10);
                        sum += p;
                        if (t >= _shortFrom) sumShort += p;
                        if (e > OnDb) on++;
                    }
                    _meanDb[k] = 10 * Math.Log10(sum / _n);
                    _shortDb[k] = 10 * Math.Log10(sumShort / Math.Max(1, _n - _shortFrom));
                    _active[k] = (double)on / _n;
                }
            }

            private double E(int t, int k) => _f[t][k] - _floor[k];

            /// <summary>
            /// The mean noise power in each bin, in dB. Each bin's own 20th
            /// percentile over time follows the receive filter's shape and
            /// ignores signals that come and go. A signal that never stops
            /// (a carrier, PSK, an MFSK mode) would lift its own bins, so no
            /// bin's floor may sit far above the median of its neighbours.
            /// Exponential noise's 20th percentile is 6.5 dB under its mean.
            /// </summary>
            private double[] NoiseFloor()
            {
                var p20 = new double[_bins];
                var col = new float[_n];
                for (int k = 0; k < _bins; k++)
                {
                    for (int t = 0; t < _n; t++) col[t] = _f[t][k];
                    Array.Sort(col);
                    p20[k] = col[(int)(0.2 * (_n - 1))];
                }
                var floor = new double[_bins];
                const int half = 80;
                var window = new List<double>(2 * half + 1);
                for (int k = 0; k < _bins; k++)
                {
                    window.Clear();
                    for (int j = Math.Max(_lo, k - half); j <= Math.Min(_hi, k + half); j++) window.Add(p20[j]);
                    double local = window.Count > 0 ? Median(window) : p20[k];
                    floor[k] = Math.Min(p20[k], local + 3) + 6.5;
                }
                return floor;
            }

            public IReadOnlyList<AudioModeGuess> Run(IReadOnlyList<AudioDecodeHint> hints)
            {
                var segments = FindSegments().Select(Measure).ToList();
                var guesses = new List<AudioModeGuess>();
                var used = new bool[segments.Count];

                // Two separate tones a known RTTY shift apart, both on most of the time.
                for (int i = 0; i < segments.Count; i++)
                {
                    if (used[i]) continue;
                    for (int j = i + 1; j < segments.Count; j++)
                    {
                        if (used[j]) continue;
                        var a = segments[i];
                        var b = segments[j];
                        double shift = b.PeakHz - a.PeakHz;
                        if (a.WidthHz > 80 || b.WidthHz > 80) continue;
                        if (a.DutyShort < 0.5 || b.DutyShort < 0.5) continue;
                        if (Math.Abs(a.PeakDb - b.PeakDb) > 10) continue;
                        double? known = MatchShift(shift);
                        if (known is null) continue;
                        if (HintIn(hints, a.LowHz, b.HighHz) is { } h)
                            guesses.Add(new(a.LowHz, b.HighHz, h.Mode, "WSJT-X decoded it here", true));
                        else
                            guesses.Add(new(a.LowHz, b.HighHz, "RTTY", $"two tones {shift:0} Hz apart ({known:0} Hz shift)", false));
                        used[i] = used[j] = true;
                        break;
                    }
                }

                for (int i = 0; i < segments.Count; i++)
                {
                    if (used[i]) continue;
                    var s = segments[i];
                    if (HintIn(hints, s.LowHz, s.HighHz) is { } h)
                        guesses.Add(new(s.LowHz, s.HighHz, h.Mode, "WSJT-X decoded it here", true));
                    else if (Guess(s) is { } g)
                        guesses.Add(g);
                }
                return guesses.OrderBy(g => g.LowHz).ToList();
            }

            private static AudioDecodeHint? HintIn(IReadOnlyList<AudioDecodeHint> hints, double low, double high)
            {
                foreach (var h in hints)
                    if (h.OffsetHz >= low - 15 && h.OffsetHz <= high + 15) return h;
                return null;
            }

            private static double? MatchShift(double hz)
            {
                foreach (var s in RttyShifts)
                    if (Math.Abs(hz - s) <= s * 0.12) return s;
                return null;
            }

            // ── finding signals ───────────────────────────────────────────────

            private List<(int Lo, int Hi)> FindSegments()
            {
                var list = new List<(int, int)>();
                int start = -1, lastOn = -1;
                for (int k = _lo; k <= _hi + 1; k++)
                {
                    bool on = k <= _hi && (_active[k] >= 0.05 || _meanDb[k] >= 2.0);
                    if (on)
                    {
                        if (start < 0) start = k;
                        lastOn = k;
                    }
                    else if (start >= 0 && k - lastOn > 2)
                    {
                        if (lastOn - start >= 1) list.Add((start, lastOn));
                        start = -1;
                    }
                }
                return list;
            }

            private sealed class Segment
            {
                public double LowHz, HighHz, PeakHz, WidthHz, PeakDb;
                public double DutyLong, DutyShort, KeyingPerSecond;
                public List<(double Start, double End, bool Whole)> Runs = new();
                public List<(double Hz, double Db)> Comb = new();
                public double CombSpacing;
                public int CombTones;
                public bool CombRegular;
            }

            private Segment Measure((int Lo, int Hi) seg)
            {
                var s = new Segment();
                int peak = seg.Lo;
                for (int k = seg.Lo; k <= seg.Hi; k++) if (_meanDb[k] > _meanDb[peak]) peak = k;
                s.PeakDb = _meanDb[peak];
                s.PeakHz = peak * _c.BinHz;

                // Width: the bins within 15 dB of the peak.
                int wLo = peak, wHi = peak;
                for (int k = seg.Lo; k <= seg.Hi; k++)
                    if (_meanDb[k] >= s.PeakDb - 15) { wLo = Math.Min(wLo, k); wHi = Math.Max(wHi, k); }
                s.LowHz = (wLo - 0.5) * _c.BinHz;
                s.HighHz = (wHi + 0.5) * _c.BinHz;
                s.WidthHz = s.HighHz - s.LowHz;

                // On or off in each frame, with hysteresis so noise at the edge doesn't chatter.
                var on = new bool[_n];
                bool state = false;
                for (int t = 0; t < _n; t++)
                {
                    double env = double.MinValue;
                    for (int k = wLo; k <= wHi; k++) env = Math.Max(env, E(t, k));
                    state = state ? env > OffDb : env > OnDb;
                    on[t] = state;
                }
                s.DutyLong = on.Count(x => x) / (double)_n;
                s.DutyShort = on.Skip(_shortFrom).Count(x => x) / (double)Math.Max(1, _n - _shortFrom);

                int keyFrom = Math.Max(1, _n - (int)Math.Round(KeyingSeconds / _c.FrameSeconds));
                int changes = 0;
                for (int t = keyFrom; t < _n; t++) if (on[t] != on[t - 1]) changes++;
                s.KeyingPerSecond = changes / ((_n - keyFrom) * _c.FrameSeconds);

                s.Runs = Runs(on);
                MeasureComb(s, wLo, wHi);
                SearchComb(s, wLo, wHi, on);
                return s;
            }

            /// <summary>Bursts of signal, gaps under 0.6 s filled, in seconds since UTC midnight.</summary>
            private List<(double, double, bool)> Runs(bool[] on)
            {
                int fill = (int)Math.Round(0.6 / _c.FrameSeconds);
                var runs = new List<(double, double, bool)>();
                int t = 0;
                while (t < _n)
                {
                    if (!on[t]) { t++; continue; }
                    int start = t, end = t;
                    while (t < _n)
                    {
                        if (on[t]) { end = t; t++; continue; }
                        int gap = t;
                        while (gap < _n && !on[gap]) gap++;
                        if (gap < _n && gap - t <= fill) { t = gap; continue; }
                        break;
                    }
                    bool whole = start > 0 && end < _n - 1;
                    // A frame's block ends at its time stamp; its middle is half a block earlier.
                    runs.Add((Seconds(_t[start]), Seconds(_t[end]), whole));
                    t = end + 1;
                }
                return runs;
            }

            private static double Seconds(DateTime utc) => utc.TimeOfDay.TotalSeconds;

            /// <summary>The tones in the signal: local peaks in its recent average spectrum.</summary>
            private void MeasureComb(Segment s, int wLo, int wHi)
            {
                double top = double.MinValue;
                for (int k = wLo; k <= wHi; k++) top = Math.Max(top, _shortDb[k]);
                var peaks = new List<(double Hz, double Db)>();
                for (int k = Math.Max(_lo + 1, wLo); k <= Math.Min(_hi - 1, wHi); k++)
                {
                    double v = _shortDb[k];
                    if (v < top - 12 || v < 4) continue;
                    if (v <= _shortDb[k - 1] || v < _shortDb[k + 1]) continue;
                    // Parabolic fit through the peak and its neighbours.
                    double a = _shortDb[k - 1], b = v, c = _shortDb[k + 1];
                    double den = a - 2 * b + c;
                    double off = den != 0 ? 0.5 * (a - c) / den : 0;
                    peaks.Add(((k + off) * _c.BinHz, v));
                }
                s.Comb = peaks;
            }

            /// <summary>
            /// Looks for evenly spaced tones. A tone sent for a symbol of 30 to
            /// 60 ms spreads as wide as the gap to the next tone, so the average
            /// spectrum shows hardly any comb. Each frame's strongest peaks do
            /// sit on tones, though: their frequencies, read between bins,
            /// pile up in a histogram at the tones and nowhere else. Then every
            /// spacing from 9 to 70 Hz and every offset is tried; the right one
            /// has the histogram high on each tone and low halfway between.
            /// The halfway points stop twice the spacing scoring as well, since
            /// they land on tones.
            /// </summary>
            private void SearchComb(Segment s, int wLo, int wHi, bool[] on)
            {
                const double res = 0.5;
                int cells = (int)Math.Ceiling((s.HighHz - s.LowHz) / res) + 1;
                var hist = new double[cells];
                int frames = 0;
                for (int t = 0; t < _n; t++)
                {
                    if (!on[t]) continue;
                    frames++;
                    double max = double.MinValue;
                    for (int k = wLo; k <= wHi; k++) max = Math.Max(max, E(t, k));
                    for (int k = Math.Max(wLo, _lo + 1); k <= Math.Min(wHi, _hi - 1); k++)
                    {
                        double v = E(t, k);
                        if (v < max - 6 || v < OnDb || v <= E(t, k - 1) || v < E(t, k + 1)) continue;
                        double a = E(t, k - 1), c = E(t, k + 1), den = a - 2 * v + c;
                        double off = den != 0 ? Math.Clamp(0.5 * (a - c) / den, -0.5, 0.5) : 0;
                        double hz = (k + off) * _c.BinHz;
                        // Spread each hit over a couple of cells.
                        for (int d = -3; d <= 3; d++)
                        {
                            int cell = (int)Math.Round((hz - s.LowHz) / res) + d;
                            if (cell >= 0 && cell < cells) hist[cell] += Math.Exp(-0.5 * d * d / 2.25);
                        }
                    }
                }
                if (frames < 20) return;
                double mean = hist.Average();
                if (mean <= 0) return;

                double HistAt(double hz)
                {
                    double x = (hz - s.LowHz) / res;
                    int i = Math.Clamp((int)Math.Floor(x), 0, cells - 2);
                    double frac = Math.Clamp(x - i, 0, 1);
                    return hist[i] + (hist[i + 1] - hist[i]) * frac;
                }

                (double Score, double Phase) ScoreAt(double sp)
                {
                    double best = 0, bestPh = 0;
                    for (double ph = 0; ph < sp; ph += 0.5)
                    {
                        double hi = 0, lo = 0;
                        int n = 0;
                        for (double f = s.LowHz + ph; f + sp / 2 <= s.HighHz; f += sp)
                        {
                            hi += HistAt(f);
                            lo += HistAt(f + sp / 2);
                            n++;
                        }
                        if (n < 4) continue;
                        double score = (hi - lo) / n / mean;
                        if (score > best) { best = score; bestPh = ph; }
                    }
                    return (best, bestPh);
                }

                double bestScore = 0, bestSpacing = 0, bestPhase = 0;
                for (double sp = 9; sp <= 70; sp += 0.125)
                {
                    if ((s.HighHz - s.LowHz) / sp < 3) break;
                    var (score, ph) = ScoreAt(sp);
                    if (score > bestScore) { bestScore = score; bestSpacing = sp; bestPhase = ph; }
                }
                if (bestScore < 1.0) return;

                // In a fast mode several symbols share a frame and their peaks
                // can land between tones, so a multiple of the real spacing may
                // score best. A spacing a known mode uses wins if it scores at
                // least half as well.
                double knownScore = 0;
                foreach (double known in KnownSpacings)
                {
                    if ((s.HighHz - s.LowHz) / known < 3) continue;
                    var (score, ph) = ScoreAt(known);
                    if (score >= 1.0 && score >= 0.5 * bestScore && score > knownScore)
                    {
                        knownScore = score;
                        bestSpacing = known;
                        bestPhase = ph;
                    }
                }

                // Tones: the comb's teeth that caught a fair share of the hits.
                double top = 0;
                for (double f = s.LowHz + bestPhase; f <= s.HighHz; f += bestSpacing) top = Math.Max(top, HistAt(f));
                int tones = 0;
                for (double f = s.LowHz + bestPhase; f <= s.HighHz; f += bestSpacing)
                    if (HistAt(f) >= top * 0.25) tones++;
                s.CombSpacing = bestSpacing;
                s.CombTones = tones;
                s.CombRegular = tones >= 4;
            }

            // ── the rules ─────────────────────────────────────────────────────

            private AudioModeGuess? Guess(Segment s)
            {
                AudioModeGuess Make(string mode, string detail) => new(s.LowHz, s.HighHz, mode, detail, false);

                if (OnClock(s, 15, 11.0, 14.0, 2.5))
                    return s.WidthHz <= 75
                        ? Make("FT8", "about 50 Hz wide, bursts of 13 s on the 15 s clock")
                        : Make("FT8", "bursts on the 15 s clock; more than one signal here");
                if (OnClock(s, 7.5, 4.5, 6.5, 1.5))
                    return Make("FT4", "about 90 Hz wide, bursts on the 7.5 s clock");

                // Two strong tones in one signal, a known RTTY shift apart.
                var strong = s.Comb.Where(p => p.Db >= s.Comb.Max(q => q.Db) - 6).ToList();
                if (strong.Count == 2 && s.DutyShort >= 0.5 && MatchShift(strong[1].Hz - strong[0].Hz) is { } shift)
                    return Make("RTTY", $"two tones {strong[1].Hz - strong[0].Hz:0} Hz apart ({shift:0} Hz shift)");

                if (s.WidthHz <= 35)
                {
                    if (s.DutyShort >= 0.95 && s.KeyingPerSecond < 0.3)
                        return Make("Carrier", "one steady tone: a tune-up, a beacon carrier or a birdie");
                    if (s.KeyingPerSecond >= 0.4 && s.DutyLong is > 0.1 and < 0.92)
                        return Make("CW", $"one narrow tone keyed on and off ({s.KeyingPerSecond:0.0} changes a second)");
                    return Make("Unknown", "one narrow tone");
                }

                if (s.DutyShort >= 0.6 && s.CombRegular)
                    return MultiTone(s, s.CombTones, s.CombSpacing, Make);

                if (s.DutyShort >= 0.8 && s.WidthHz <= 170)
                {
                    string name = s.WidthHz <= 55 ? "PSK31" : s.WidthHz <= 95 ? "PSK63" : "PSK125";
                    return Make(name, $"one smooth signal about {s.WidthHz:0} Hz wide, on all the time");
                }

                if (s.WidthHz >= 300 && s.DutyLong >= 0.2)
                    return Make("Voice?", $"{s.WidthHz:0} Hz wide and changing: SSB voice or another wideband signal");

                return Make("Unknown", $"about {s.WidthHz:0} Hz wide");
            }

            private static AudioModeGuess MultiTone(Segment s, int tones, double spacing,
                Func<string, string, AudioModeGuess> make)
            {
                string shown = spacing >= 19 ? spacing.ToString("0") : spacing.ToString("0.#");
                string detail = $"{tones} tones {shown} Hz apart";
                bool Near(double want, double tol) => Math.Abs(spacing - want) <= tol;

                if (Near(20, 3))
                {
                    if (tones is >= 5 and <= 7) return make("Piccolo", detail + " (Piccolo has 6, 20 Hz apart)");
                    if (tones is >= 10 and <= 13) return make("Piccolo", detail + " (Piccolo 12-tone, 20 Hz apart)");
                }
                if (Near(15.625, 2.5) && tones is >= 14 and <= 18) return make("MFSK16", detail);
                if (Near(31.25, 4))
                {
                    int n = Nearest(tones, 4, 8, 16, 32);
                    if (Math.Abs(n - tones) <= Math.Max(1, n / 8))
                        return make($"Olivia {n}/{(int)Math.Round(n * 31.25)}", detail + " (or Contestia)");
                }
                if (Near(62.5, 6))
                {
                    int n = Nearest(tones, 4, 8, 16);
                    if (Math.Abs(n - tones) <= Math.Max(1, n / 8))
                        return make($"Olivia {n}/{(int)Math.Round(n * 62.5)}", detail + " (or Contestia)");
                }
                return make("Multi-tone", detail);
            }

            private static int Nearest(int value, params int[] choices) =>
                choices.OrderBy(c => Math.Abs(c - value)).First();

            /// <summary>
            /// True when a whole burst of the right length starts within slack
            /// seconds of a multiple of period (UTC), as FT8 and FT4 do.
            /// </summary>
            private static bool OnClock(Segment s, double period, double minLen, double maxLen, double slack)
            {
                foreach (var (start, end, whole) in s.Runs)
                {
                    if (!whole) continue;
                    double len = end - start;
                    if (len < 0) len += 86400;
                    if (len < minLen || len > maxLen) continue;
                    double phase = start % period;
                    if (phase <= slack || phase >= period - 0.5) return true;
                }
                return false;
            }

            private static double Median(List<double> values)
            {
                var sorted = values.OrderBy(v => v).ToList();
                int m = sorted.Count / 2;
                return sorted.Count % 2 == 1 ? sorted[m] : 0.5 * (sorted[m - 1] + sorted[m]);
            }
        }
    }
}
