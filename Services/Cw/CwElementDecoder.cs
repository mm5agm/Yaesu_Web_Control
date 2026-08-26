using System.Text;

namespace RadioWebControl.Core.Services.Cw
{
    public sealed class CwElementDecoderOptions
    {
        /// <summary>Fastest fist to track, WPM. Sets the lower clamp on the dit estimate.</summary>
        public double MaxWpm { get; set; } = 60.0;

        /// <summary>Slowest fist to track, WPM. Sets the upper clamp on the dit estimate.</summary>
        public double MinWpm { get; set; } = 5.0;

        /// <summary>Starting guess before anything has been heard, WPM.</summary>
        public double InitialWpm { get; set; } = 20.0;

        /// <summary>
        /// Marks and gaps shorter than this are glitches, not elements, and are
        /// swallowed. 12 ms is under half a dit even at 60 WPM.
        /// </summary>
        public double MinElementMs { get; set; } = 12.0;

        /// <summary>Flush a part-built character after this much silence.</summary>
        public double IdleFlushMs { get; set; } = 1500.0;

        /// <summary>
        /// How far above the tracked noise floor a mark's peak must sit before
        /// it may train the speed, as a multiple.
        ///
        /// Not the presence gate, which cannot do this job: the detector
        /// already forces key-up when the gate says absent, so every mark the
        /// element decoder ever sees arrives with SignalPresent true - measured,
        /// 509 of 509 across three recordings. Noise marks live inside presence
        /// excursions. What separates them is level. Measured over four
        /// recordings, marks that are really elements sit at five to seven
        /// times the noise floor and noise blips at two to three, which is
        /// what puts the line here.
        /// </summary>
        public double MinTrainNoiseMultiple { get; set; } = 3.5;

        /// <summary>
        /// Allow the hard re-seed that fires after three marks in a row miss
        /// their centroid. Off is a measurement setting: it separates the
        /// re-seed from the EMA when the tracked speed runs away.
        /// </summary>
        public bool EnableResync { get; set; } = true;
    }

    /// <summary>
    /// Adaptive element timing: key-down/key-up durations in, characters out.
    ///
    /// The important decision here is that marks are clustered against two
    /// tracked centroids - a dit length and a dah length, compared in log space -
    /// rather than against a boundary derived from a WPM number somebody typed in.
    /// That is the whole difference between this and the decoder in the FTdx101,
    /// which takes its reference from the TX keyer speed knob and therefore cannot
    /// follow the other operator when an over changes hands. Comparing in log
    /// space matters in both directions: when the speed rises, a new dah can be
    /// shorter than twice the old dit and a fixed boundary would call it a dit.
    ///
    /// The centroids are pulled weakly toward a 3:1 ratio so that a run of all
    /// dits or all dahs still tracks, while a heavy fist can still sit off ratio.
    /// A hard resync catches the case where the speed jumps far enough that the
    /// EMA would crawl.
    /// </summary>
    public sealed class CwElementDecoder
    {
        private readonly CwElementDecoderOptions _opt;
        private readonly StringBuilder _symbol = new();
        private readonly double[] _recentMarks = new double[3];

        private double _ditMs;
        private double _dahMs;
        private double _minDitMs;
        private double _maxDitMs;

        private bool   _keyDown;
        private double _edgeTimeMs;          // when the current run started
        private bool   _started;

        private double _runPeakMag;          // loudest sample of the run in progress
        private double _runNoiseSum;
        private int    _runNoiseCount;

        private int  _marksSeen;
        private int  _recentCount;
        private int  _consecutiveOutliers;
        private bool _pendingCharGap;        // a character has been flushed, awaiting word gap
        private int  _unknownSymbols;

        public CwElementDecoder(CwElementDecoderOptions? options = null)
        {
            _opt = options ?? new CwElementDecoderOptions();
            _ditMs    = 1200.0 / _opt.InitialWpm;
            _dahMs    = _ditMs * 3.0;
            _minDitMs = 1200.0 / _opt.MaxWpm;
            _maxDitMs = 1200.0 / _opt.MinWpm;
        }

        /// <summary>Current tracked speed, from the dit estimate.</summary>
        public double WordsPerMinute => 1200.0 / _ditMs;

        /// <summary>Current dit estimate, milliseconds.</summary>
        public double DitMs => _ditMs;

        /// <summary>True once enough elements have been seen for the speed to mean something.</summary>
        public bool IsLocked => _marksSeen >= 6;

        /// <summary>Symbols that timed out into something not in the Morse table.</summary>
        public int UnknownSymbolCount => _unknownSymbols;

        /// <summary>
        /// Feed one envelope observation. Returns any text completed by it, which
        /// is usually the empty string.
        /// </summary>
        public string Push(in CwToneSample sample)
        {
            double tMs = sample.TimeSeconds * 1000.0;

            if (!_started)
            {
                _started    = true;
                _keyDown    = sample.KeyDown;
                _edgeTimeMs = tMs;
                return string.Empty;
            }


            if (_keyDown)
            {
                if (sample.Magnitude > _runPeakMag) _runPeakMag = sample.Magnitude;
                _runNoiseSum += sample.NoiseLevel;
                _runNoiseCount++;
            }

            if (sample.KeyDown == _keyDown)
            {
                // Still in the same run. The only thing that can happen mid-run is
                // a long silence deciding that the transmission has ended.
                if (!_keyDown && _symbol.Length > 0 && tMs - _edgeTimeMs >= _opt.IdleFlushMs)
                {
                    var flushed = FlushCharacter();
                    _edgeTimeMs = tMs;          // do not flush again on the next sample
                    return flushed;
                }
                return string.Empty;
            }

            double durationMs = tMs - _edgeTimeMs;
            _edgeTimeMs = tMs;
            bool wasKeyDown = _keyDown;
            _keyDown = sample.KeyDown;

            // De-glitch. A run too short to be an element is noise; undo the edge
            // so the two runs either side of it join up.
            if (durationMs < _opt.MinElementMs)
            {
                _keyDown = wasKeyDown;
                _edgeTimeMs = tMs - durationMs;
                return string.Empty;
            }

            double peak  = _runPeakMag;
            double noise = _runNoiseCount > 0 ? _runNoiseSum / _runNoiseCount : 0.0;
            _runPeakMag    = sample.Magnitude;
            _runNoiseSum   = sample.NoiseLevel;
            _runNoiseCount = 1;

            return wasKeyDown ? OnMark(durationMs, peak, noise) : OnGap(durationMs);
        }

        /// <summary>
        /// Force out whatever is part-built. Called when capture stops so the last
        /// character is not lost.
        /// </summary>
        public string Flush()
            => _symbol.Length > 0 ? FlushCharacter() : string.Empty;

        private string OnMark(double markMs, double peakMag, double noiseLevel)
        {
            bool isDah = ClassifyMark(markMs, out double distance);
            _symbol.Append(isDah ? '-' : '.');

            // The symbol still goes out - whether to suppress text is a separate
            // question - but a mark far below the level real elements have been
            // arriving at does not get a vote on how fast the other operator is
            // sending. Those are the ones that rail the estimate at MaxWpm.
            if (TrainOn(peakMag, noiseLevel))
            {
                if (_opt.EnableResync) TrackResync(markMs, distance);
                UpdateCentroids(markMs, isDah);
                _marksSeen++;
            }

            _pendingCharGap = false;

            return string.Empty;
        }

        /// <summary>
        /// Is this mark far enough above the noise floor to be worth learning
        /// the speed from? Marks that are really elements measured five to
        /// seven times the noise floor across four off-air recordings; noise
        /// blips measured two to three, and a relative test against other marks
        /// measured no better once this one was in.
        /// </summary>
        private bool TrainOn(double peakMag, double noiseLevel)
            => _opt.MinTrainNoiseMultiple <= 0.0
            || noiseLevel <= 1e-12
            || peakMag >= _opt.MinTrainNoiseMultiple * noiseLevel;

        private string OnGap(double gapMs)
        {
            // 1 / 3 / 7 dits, split at the midpoints in log-ish terms: anything
            // under 2 units is inside a character, under 5 units is between
            // characters, beyond that is between words.
            double unit = _ditMs;

            if (gapMs < 2.0 * unit) return string.Empty;

            var text = FlushCharacter();

            if (gapMs >= 5.0 * unit && !_pendingCharGap)
            {
                text += " ";
                _pendingCharGap = true;
            }

            return text;
        }

        private string FlushCharacter()
        {
            if (_symbol.Length == 0) return string.Empty;

            string sym = _symbol.ToString();
            _symbol.Clear();

            var decoded = MorseTable.Decode(sym);
            if (decoded is null) { _unknownSymbols++; return string.Empty; }
            return decoded;
        }

        /// <summary>
        /// Nearest centroid in log space. Returns true for a dah, and reports how
        /// far off the winning centroid the mark was, as a log ratio.
        /// </summary>
        private bool ClassifyMark(double markMs, out double distance)
        {
            double dDit = Math.Abs(Math.Log(markMs / _ditMs));
            double dDah = Math.Abs(Math.Log(markMs / _dahMs));

            if (dDah < dDit) { distance = dDah; return true; }
            distance = dDit;
            return false;
        }

        private void UpdateCentroids(double markMs, bool isDah)
        {
            // Converge quickly while unlocked, then settle down. This constant is
            // the re-acquisition/stability trade the plan asks to be measured
            // rather than guessed: raise it and a speed change is picked up in
            // fewer characters, at the cost of a ragged fist dragging it about.
            double alpha = _marksSeen < 8 ? 0.45 : 0.22;

            if (isDah) _dahMs += alpha * (markMs - _dahMs);
            else       _ditMs += alpha * (markMs - _ditMs);

            // Weak pull toward 3:1 so a run of one element type still moves both.
            double unit = 0.5 * (_ditMs + _dahMs / 3.0);
            _ditMs += 0.20 * (unit - _ditMs);
            _dahMs += 0.20 * (3.0 * unit - _dahMs);

            Clamp();
        }

        private void TrackResync(double markMs, double distance)
        {
            _recentMarks[_recentCount % _recentMarks.Length] = markMs;
            _recentCount++;

            // ln(1.8) = 0.588. Three marks in a row that far from the centroid they
            // matched means the speed moved further than the EMA will catch up with
            // in reasonable time, so re-seed from what we just heard: the shortest
            // of the three is very probably a dit.
            if (distance > 0.588) _consecutiveOutliers++;
            else _consecutiveOutliers = 0;

            if (_consecutiveOutliers < 3 || _recentCount < _recentMarks.Length) return;

            double shortest = _recentMarks[0];
            for (int i = 1; i < _recentMarks.Length; i++)
                shortest = Math.Min(shortest, _recentMarks[i]);

            _ditMs = shortest;
            _dahMs = shortest * 3.0;
            _consecutiveOutliers = 0;
            Clamp();
        }

        private void Clamp()
        {
            _ditMs = Math.Clamp(_ditMs, _minDitMs, _maxDitMs);
            _dahMs = Math.Clamp(_dahMs, _minDitMs * 2.0, _maxDitMs * 4.0);
        }
    }
}
