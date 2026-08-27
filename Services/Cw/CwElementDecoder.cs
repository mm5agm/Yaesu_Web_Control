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

        /// <summary>
        /// How many recent marks the readability test looks at.
        ///
        /// Long enough that a genuine run of one element type cannot fill it -
        /// the longest all-one-element characters are 5 and 0, at five marks
        /// each - and short enough that the test recovers promptly once a
        /// signal comes back out of a fade. That second requirement is what
        /// set it: at 32 the window was still full of the fade when the signal
        /// returned, and the QSB tests lost most of their text. Characters
        /// surviving the gate on the two recordings the operator reported good
        /// copy on, against what the decoder produced ungated:
        ///
        ///   window   dk9py (262)   i1yrl (270)
        ///      32     217 (83%)     256 (95%)
        ///      16     261 (99%)     266 (98%)
        /// </summary>
        public int ReadabilityWindow { get; set; } = 16;

        /// <summary>
        /// Fewest marks in the window before readability is judged at all.
        /// Below this there is not enough evidence either way, and the answer
        /// is "not yet", not "unreadable" - a decoder that said "nothing
        /// readable" the instant it started would be lying.
        /// </summary>
        public int ReadabilityMinMarks { get; set; } = 10;

        /// <summary>
        /// The p90/p10 band of recent mark lengths that can be Morse.
        ///
        /// Morse has two lengths in a 3:1 ratio, so this number sits near 3 on
        /// anything readable, and it does so without knowing the speed - which
        /// is the point, because the speed estimate is one of the first things
        /// to fail. Measured 2026-08-26 over whole files in the bench corpus:
        ///
        ///   mkii-dk9py     3.00   operator reported good copy
        ///   mkii-i1yrl     3.33   operator reported good copy
        ///   probe15        1.33   all-dit garbage
        ///   probe15b       1.33   all-dit garbage
        ///   ywc-40m-cw     8.67   several stations in the passband
        ///
        /// Below the floor there is only one mark length, so nothing can ever
        /// be classified as a dah and every character comes out of the all-dit
        /// set - E I S H 5. That is the tone detector chattering across its
        /// hysteresis on a near-threshold carrier, not a fist. Both probe
        /// files sat at a 15 ms median, which is the de-glitch floor itself:
        /// the decoder was counting its own glitches.
        ///
        /// The floor is the log-space midpoint between the good files at 3.0
        /// and the chattering ones at 1.33, which lands on 2.0. It does all
        /// the work: it takes probe15 and probe15b from 160 and 211 characters
        /// of garbage to none at all, and costs the good files one character
        /// and four.
        ///
        /// The ceiling is meant for the other failure, several stations in one
        /// passband, where the marks have no two-length structure at all. Be
        /// aware that at the current window size it is very nearly inert -
        /// swept over the corpus it moved one file by two characters:
        ///
        ///   ceiling   dk9py   i1yrl   40m QRM   probe15
        ///       6.0     261     266       165         0
        ///       8.0     261     266       165         0
        ///      12.0     261     266       167         0
        ///
        /// It is kept because it costs nothing and names the second failure
        /// explicitly, not because it has been shown to earn its place. If a
        /// heavy or swinging fist is ever reported as Jumbled, loosen it
        /// without hesitation.
        /// </summary>
        public double ReadabilitySpreadFloor { get; set; } = 2.0;

        /// <inheritdoc cref="ReadabilitySpreadFloor"/>
        public double ReadabilitySpreadCeiling { get; set; } = 8.0;
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
    /// <summary>
    /// Whether the marks arriving can be Morse at all - a question separate
    /// from whether the tone is being tracked well, which is what
    /// <c>Confidence</c> answers.
    ///
    /// The two came apart on bench/probe15.wav: pinning the tone raised
    /// confidence to 1.00 while the transcript stayed a stream of E I S H 5.
    /// Confidence was right - the tone was exactly where it said - and the
    /// text was still worthless, because there was no keying behind it. An
    /// application that shows text whenever confidence is high will show that
    /// stream, and an operator has no way to tell it from a bad copy of a real
    /// station.
    /// </summary>
    public enum CwReadability
    {
        /// <summary>Not enough marks yet to say. Show nothing rather than guess.</summary>
        Unknown,

        /// <summary>Mark lengths have the two-length structure Morse has.</summary>
        Readable,

        /// <summary>
        /// One mark length only. Nothing can be a dah, so every character
        /// falls in the all-dit set. The detector is chattering, not copying.
        /// </summary>
        Chatter,

        /// <summary>
        /// Mark lengths are scattered far wider than one fist produces -
        /// several stations inside the passband, most likely.
        /// </summary>
        Jumbled,
    }

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

        private readonly double[] _markWindow;
        private int _markWindowCount;
        private int _markWindowNext;

        public CwElementDecoder(CwElementDecoderOptions? options = null)
        {
            _opt = options ?? new CwElementDecoderOptions();
            _markWindow = new double[Math.Max(4, _opt.ReadabilityWindow)];
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

        /// <summary>
        /// Whether the recent marks can be Morse at all. See
        /// <see cref="CwReadability"/> - this is not the same question as
        /// <c>Confidence</c>, and on a chattering detector the two disagree.
        /// </summary>
        public CwReadability Readability => AssessReadability(out _);

        /// <summary>
        /// p90/p10 of the recent mark lengths, or 0 before there are enough.
        /// Near 3 on readable Morse, because that is the dah:dit ratio.
        /// </summary>
        public double MarkSpread { get { AssessReadability(out double spread); return spread; } }

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
            // Every mark goes into the readability window, including ones too
            // weak to train the speed. The question the window answers is
            // "what shape is the thing arriving", and marks excluded from
            // training are exactly the ones that give the fault away.
            _markWindow[_markWindowNext] = markMs;
            _markWindowNext = (_markWindowNext + 1) % _markWindow.Length;
            if (_markWindowCount < _markWindow.Length) _markWindowCount++;

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

        /// <summary>
        /// Sort the recent marks and compare the 90th percentile with the
        /// 10th. Percentiles rather than min and max because a single glitch
        /// at either end would otherwise decide the answer, and the whole
        /// point is to characterise the population.
        /// </summary>
        private CwReadability AssessReadability(out double spread)
        {
            spread = 0.0;
            if (_markWindowCount < _opt.ReadabilityMinMarks) return CwReadability.Unknown;

            Span<double> sorted = stackalloc double[_markWindowCount];
            for (int i = 0; i < _markWindowCount; i++) sorted[i] = _markWindow[i];
            sorted.Sort();

            double p10 = sorted[(int)(0.10 * (_markWindowCount - 1))];
            double p90 = sorted[(int)(0.90 * (_markWindowCount - 1))];
            if (p10 <= 0.0) return CwReadability.Unknown;

            spread = p90 / p10;
            if (spread < _opt.ReadabilitySpreadFloor)   return CwReadability.Chatter;
            if (spread > _opt.ReadabilitySpreadCeiling) return CwReadability.Jumbled;
            return CwReadability.Readable;
        }

        private void Clamp()
        {
            _ditMs = Math.Clamp(_ditMs, _minDitMs, _maxDitMs);
            _dahMs = Math.Clamp(_dahMs, _minDitMs * 2.0, _maxDitMs * 4.0);
        }
    }
}
