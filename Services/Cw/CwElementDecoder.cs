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
        /// How long a mark may still count towards readability, in seconds.
        ///
        /// Without this the windows only ever fill: every count rises to the
        /// ring's capacity and never falls, so one readable burst latches
        /// Readable for the rest of the session. Measured on air 2026-08-29,
        /// a station read Readable for minutes after it had stopped sending,
        /// and CwDecoderEngine.Gate releases held text on exactly that
        /// verdict - so noise dits reached the screen as copy.
        ///
        /// Ten seconds without a single mark is a station that has stopped,
        /// at any speed. While marks keep arriving the window is judged whole,
        /// exactly as it was before. Only the Readable verdict is withdrawn -
        /// see AssessReadability for why Chatter and Jumbled are left to
        /// stand.
        /// </summary>
        public double ReadabilityMaxAgeSeconds { get; set; } = 10.0;

        /// <summary>
        /// The same idea for the dit-only character window, which needs a
        /// longer horizon because whole characters arrive far more slowly
        /// than marks - at 5 wpm a character can take several seconds, and a
        /// ten-second silence test would retire that guard on a signal still
        /// being copied.
        /// </summary>
        public double DitOnlyMaxAgeSeconds { get; set; } = 30.0;

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

        /// <summary>
        /// Fraction of recent key-down runs that may be thrown out as too short
        /// to be an element before the copy is called
        /// <see cref="CwReadability.Jumbled"/> whatever its spread says.
        ///
        /// This exists because the spread on its own can be fooled. The
        /// de-glitch removes exactly the short outliers that make p10 small,
        /// so a channel full of blips gets its evidence cleaned away and comes
        /// out looking like tidy sending; measured on 2026-08-27, 20 WPM at
        /// 3 dB went from 43% Jumbled to 99% "Readable" while the copy was
        /// still wrong. Feeding the discarded blips back into the spread was
        /// tried first and is worse: p90/p10 is a ratio, and a noise blip
        /// population spanning one to four hops has the same 3:1 ratio a
        /// dit-and-dah population does, so an empty band decoded as
        /// "II EIES&lt;HH&gt; IEE" and called it Readable. Counting the
        /// discards instead cannot be imitated by scale.
        /// </summary>
        public double ReadabilityDirtyCeiling { get; set; } = 0.35;

        /// <summary>
        /// Share of recent characters that may be built from dits alone -
        /// E, I, S, H and 5 - before the copy is called
        /// <see cref="CwReadability.Jumbled"/>.
        ///
        /// This is the only check here that looks at what came out rather than
        /// at the timing that produced it, and it is here because the timing
        /// checks share a blind spot: a detector emitting spurious short marks
        /// produces marks that are individually plausible, in a population
        /// with a plausible spread, and the only place the fault is visible is
        /// in the text. Spurious short marks can only ever assemble into
        /// dit-only letters, so a stretch of copy that is nearly all E/I/S/H
        /// is the detector counting the band.
        ///
        /// The threshold is set from the ten ARRL W1AW practice files, whose
        /// sent text is known. Real traffic sits in a remarkably tight band -
        /// 23.3, 26.5, 27.2, 27.2, 27.5, 28.1, 28.6, 28.6, 28.7 and 30.8 per
        /// cent over 360 to 3097 letters. At the window below that is about
        /// five standard deviations from the ceiling, so ordinary text does
        /// not trip it.
        ///
        /// The ceiling was swept on 2026-08-27 against the cases where the
        /// timing checks are blind, reading accuracy and Readable/Jumbled:
        ///
        ///   ceiling   40 WPM 0 dB      30 WPM 0 dB      40 WPM 3 dB    5/20/40 clean
        ///     off     15.6%  64%/22%   15.8%  58%/26%   85.3% 98%/0%   untouched
        ///     0.70    11.6%  19%/66%    9.4%  45%/39%   85.3% 98%/0%   untouched
        ///     0.60     4.2%   3%/83%    2.4%  24%/60%   85.3% 98%/0%   untouched
        ///     0.50     0.4%   1%/85%    1.9%  22%/62%   85.3% 97%/1%   untouched
        ///
        /// 0.60 condemns the junk without touching a single case that copies
        /// well; 0.50 starts eating into 40 WPM at 3 dB, which is 85% correct
        /// and must be shown. Every clean file is identical at every setting,
        /// and so is mkii-dk9py at 275 characters.
        ///
        /// It scores, it never edits. Deleting suspect letters individually
        /// was considered and rejected: nothing distinguishes a spurious S
        /// from the S in a callsign, and a callsign is the one thing that has
        /// to be exact. Copy that has been quietly corrected into something
        /// plausible is worse than copy that is visibly wrong, because only
        /// the second tells the operator not to trust it.
        /// </summary>
        public double ReadabilityDitOnlyCeiling { get; set; } = 0.60;

        /// <summary>Characters remembered for <see cref="ReadabilityDitOnlyCeiling"/>.</summary>
        public int DitOnlyWindow { get; set; } = 48;

        /// <summary>Characters needed before that ceiling is applied at all.</summary>
        public int DitOnlyMinChars { get; set; } = 24;

        /// <summary>
        /// The fewest elements per character a stream can average and still be
        /// called Morse.
        ///
        /// The spread and dit-only tests both ask about marks. This one asks
        /// about characters, and it catches what neither can: a detector
        /// chattering on an empty band emits well-separated short and long
        /// blips - a textbook dit/dah spread - which are then flushed one per
        /// character. bench/diag-dead.wav is twelve seconds of nothing and
        /// reads "SETE ET TE E T TE E": every mark cleanly classified, every
        /// character one element long, and Readable 81% with the speed railed
        /// at the 60 WPM ceiling and the lock lit. The dit-only test cannot
        /// see it because E and T alternate, so the characters are not all
        /// dits.
        ///
        /// Real Morse cannot average near one, because the alphabet does not
        /// allow it. Over the 11,497 characters of the ARRL practice texts the
        /// sent mean is 2.67 elements per character and only 20.1% of
        /// characters are a single element - E and T are the two commonest
        /// letters in English, which is exactly why they are the two shortest.
        /// Decoded, across seven recordings from 12 to 2,489 characters and
        /// from 5 to 40 WPM, the mean ran 2.33 to 3.14. The dead band ran 1.15.
        /// There is no overlap to split, so the floor sits midway.
        ///
        /// Like the dit-only ceiling this scores and never edits - the text is
        /// still printed. What it withholds is the speed and the lock, which
        /// are the two figures an operator has no way to sanity-check.
        /// </summary>
        public double ReadabilityMinElementsPerChar { get; set; } = 1.70;

        /// <summary>
        /// Characters needed before <see cref="ReadabilityMinElementsPerChar"/>
        /// is applied. Lower than <see cref="DitOnlyMinChars"/> deliberately:
        /// the dead-band case produces only about twenty characters in twelve
        /// seconds, so a window that waits for twenty-four never engages on the
        /// one recording it exists for.
        /// </summary>
        public int MinElementsPerCharMinChars { get; set; } = 10;

        /// <summary>
        /// Where the boundary between a gap inside a character and a gap
        /// between two characters sits, in tracked dits.
        ///
        /// Textbook Morse puts the two at one dit and three, so a boundary at
        /// two is the obvious midpoint, and hardcoded at two is what this was.
        /// Measured on real audio the two clusters do not land where the
        /// textbook says. The detector opens a shade late and closes a shade
        /// early on every mark, and the time it takes off the mark it adds to
        /// the gap on either side, so the gap distribution is stretched while
        /// the marks are squeezed. On bench/live-hb9dax-cq.wav, 120 s at
        /// 13-18 dB with every mark solid, the dah:dit ratio measures 3.44
        /// rather than 3.00 and the gaps fall out as:
        ///
        ///     1.0 dits    93
        ///     1.5 dits   167     intra-character, peaking at 1.5 not 1.0
        ///     2.0 dits    21     the old boundary, on a populated bin
        ///     2.5 dits     2     the valley
        ///     3.0 dits     9
        ///     4.0 dits    22     between characters
        ///
        /// A boundary at 2.0 dits therefore cuts the shoulder of the
        /// intra-character cluster rather than the empty ground above it, and
        /// every gap it clips splits one character into two - a split
        /// character emits two short symbols in place of one, which looked
        /// like the source of the stray E, I and T in poor copy.
        ///
        /// It is not. Swept over all 99 corpus recordings, scoring correct CQ
        /// against mangled CQ:
        ///
        ///     2.0    12 correct, 0 mangled    baseline
        ///     2.25   12 correct, 0 mangled    35 files change, no CQ changes
        ///     2.5    12 correct, 0 mangled    51 files change, no CQ changes
        ///     2.75    7 correct, 0 mangled    live-ly-oe3wma 6 to 2,
        ///                                     mkii-dk9py 4 to 2
        ///
        /// Moving the boundary into the measured valley buys nothing and
        /// moving it past the valley costs five CQs. Total characters fall
        /// from 19725 to 16664 across the sweep, so the splits really are
        /// being merged; the merging just does not make the copy better. The
        /// default therefore stays at 2.0 and the histogram argument is
        /// recorded here as tried and rejected, so it does not get re-derived
        /// from the same evidence and re-tried.
        ///
        /// The knob is kept because it is what made the measurement possible,
        /// and CwBench exposes it as --char-gap.
        /// </summary>
        public double CharacterGapDits { get; set; } = 2.0;

        /// <summary>
        /// How long <see cref="CwElementDecoder.IsLocked"/> keeps believing the
        /// speed estimate after the marks stop looking like Morse.
        ///
        /// Long enough to ride out a fade, short enough that a band which has
        /// gone dead stops claiming a speed. Matched to the engine's
        /// HoldStaleSeconds so held text and the speed reading give up together
        /// rather than the panel showing a speed for something it will not print.
        /// </summary>
        public double LockHoldMs { get; set; } = 5000.0;
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
        /// <summary>
        /// How far above the character gap a gap has to be before it is a word
        /// gap. Textbook timing puts the two at seven and three dits, a ratio
        /// of 2.33, and Farnsworth keeps that ratio while stretching both - the
        /// three ARRL files above measure 3.68, 2.34 and 2.35. Splitting at
        /// 1.8 leaves room on both sides of every one of those, and still sits
        /// under the seven dits a textbook sender leaves, so an operator who
        /// runs their word gaps short is not punished for it.
        /// </summary>
        private const double WordGapRatio = 1.8;

        /// <summary>How many separator gaps before <see cref="WordGapMs"/> will
        /// try to separate two populations out of them. Below this the ratio
        /// rule is used, because a handful of gaps from the opening of a
        /// transmission are all character gaps and clustering them splits a
        /// cluster that has no split in it.</summary>
        private const int WordGapMinGaps = 12;

        /// <summary>Gaps longer than this many character gaps are clipped to it
        /// before clustering: past about here a gap is a sender pausing, not a
        /// word boundary, and it should not get a vote on where the boundary
        /// is.</summary>
        private const double WordGapClipDits = 4.0;

        /// <summary>How far apart the two cluster centres have to be before
        /// they are believed to be two things. Under this, one population.</summary>
        private const double WordGapMinSeparation = 1.20;

        /// <summary>The lowest the measured split may sit, in character gaps.
        /// Anything at or below 1.0 splits characters into words.</summary>
        private const double WordGapFloor = 1.25;

        /// <summary>The share of separators the word cluster must hold before
        /// it is believed. Plain English text puts about one separator in five
        /// at a word boundary; well outside that band the two clusters are not
        /// characters and words.</summary>
        private const double WordGapMinShare = 0.08;
        private const double WordGapMaxShare = 0.45;

        /// <summary>Two-means iterations. It converges in three or four on this
        /// data; the cap is only there so a pathological window cannot spin.</summary>
        private const int WordGapPasses = 12;

        /// <summary>
        /// The de-glitch floor as a fraction of the tracked dit, taking over
        /// from the fixed MinElementMs wherever it is the larger of the two.
        /// See the de-glitch itself for what it is protecting against.
        ///
        /// Swept 2026-08-27 against the ARRL files with noise added in 500 Hz,
        /// and against a recorded QSO. "n3" is 3 dB SNR:
        ///
        ///   frac : 05n6   13n3   20n3   30n3   40cl   dk9py
        ///   0.00 : 68.9%  30.0%  61.3%  79.8%  99.4%   247
        ///   0.30 : 68.7%  41.0%  65.1%    -    99.4%   257
        ///   0.40 : 79.6%  58.2%  72.5%  82.0%  99.4%   257
        ///   0.45 : 79.8%  59.7%  73.8%  82.7%  99.7%   252
        ///   0.50 : 79.8%  61.0%  74.2%  83.4%  99.2%   222
        ///   0.65 : 80.5%  62.1%  74.5%  85.2%  99.0%   208
        ///
        /// The synthetic noise keeps rewarding a bigger floor all the way up,
        /// and the recorded QSO is what says where to stop: dk9py peaks at 0.40
        /// and has shed 49 characters by 0.65, with the fast clean files
        /// starting to slip alongside it. Past 0.40 the floor is eating real
        /// dits, and a sweep run only against generated noise would have walked
        /// straight past that and picked 0.65.
        ///
        /// Every clean column is flat across the whole range - 97.1% at 5 WPM,
        /// 96.3% at 10, 99.4% at 20 - so none of this is bought from the
        /// undegraded case.
        ///
        /// One file dissents: 10 WPM at 3 dB reads 26.3% with no adaptive floor
        /// and 12.4% here, recovering to 23.0% by 0.65. It is not weighed
        /// heavily, because every one of those numbers is an unreadable decode
        /// and choosing between two unreadable decodes is not worth a real
        /// QSO's characters. It is recorded rather than tidied away because it
        /// is unexplained, and an unexplained dissent is worth more written
        /// down than forgotten.
        /// </summary>
        private const double MinElementDits = 0.40;

        /// <summary>Trained marks needed before the speed estimate means anything. See <see cref="IsLocked"/>.</summary>
        private const int LockMarks = 6;

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
        private bool _idleFlushed;           // this gap has already had its idle flush

        private readonly double[] _gapWindow = new double[48];
        private int _gapWindowCount;
        private int _gapWindowNext;
        private int  _unknownSymbols;

        private readonly double[] _markWindow;
        private double _lastMarkAtMs = double.NegativeInfinity;
        private int _markWindowCount;
        private int _markWindowNext;

        // Parallel to _markWindow but over every key-down run, element or not:
        // true where the run was discarded as too short. See ReadabilityDirtyCeiling.
        private readonly bool[] _runWindow;
        private double _lastRunAtMs = double.NegativeInfinity;
        private int _runWindowCount;
        private int _runWindowNext;

        // Recent decoded characters, true where the symbol held no dah.
        private readonly bool[] _ditOnly;
        private readonly byte[] _charElements;
        private double _lastDitOnlyAtMs = double.NegativeInfinity;
        private int _ditOnlyCount;
        private int _ditOnlyNext;

        // Clock and lock state. _lastReadableMs is the last time the marks
        // behind the speed estimate looked like Morse; see IsLocked.
        private double _nowMs;
        private double _lastReadableMs = double.NegativeInfinity;

        public CwElementDecoder(CwElementDecoderOptions? options = null)
        {
            _opt = options ?? new CwElementDecoderOptions();
            _markWindow = new double[Math.Max(4, _opt.ReadabilityWindow)];
            _runWindow  = new bool[Math.Max(4, _opt.ReadabilityWindow)];
            _ditOnly    = new bool[Math.Max(4, _opt.DitOnlyWindow)];
            _charElements = new byte[_ditOnly.Length];
            _ditMs    = 1200.0 / _opt.InitialWpm;
            _dahMs    = _ditMs * 3.0;
            _minDitMs = 1200.0 / _opt.MaxWpm;
            _maxDitMs = 1200.0 / _opt.MinWpm;
        }

        /// <summary>Current tracked speed, from the dit estimate.</summary>
        public double WordsPerMinute => 1200.0 / _ditMs;

        /// <summary>Current dit estimate, milliseconds.</summary>
        public double DitMs => _ditMs;

        /// <summary>
        /// True once the speed estimate is worth reporting.
        ///
        /// This used to be "six marks have trained the estimate", which is a
        /// statement about how much evidence there is and not about whether
        /// the evidence was worth anything. On an empty band the noise blips
        /// that survive de-glitch train it too: three minutes of recorded
        /// empty 15 m reported 51.4 wpm and "locked", which reads as a very
        /// fast operator rather than as a decoder with nothing to decode. A
        /// speed measured from marks that do not have the shape of Morse is
        /// not a speed, and printing it is worse than printing nothing,
        /// because the operator has no way to tell the two apart.
        ///
        /// So the lock also asks the readability question - but with a hold,
        /// rather than instant by instant. Readability dips through Chatter on
        /// any deep fade, and a lock that drops there would blink the speed
        /// off and on across normal QSB. What actually distinguishes a fade
        /// from a dead band is how long the dip lasts: a fade comes back,
        /// and a band with nothing on it never does. The hold is the same
        /// <see cref="CwElementDecoderOptions.LockHoldMs"/> horizon the engine
        /// uses to give up on held text, for the same reason.
        /// </summary>
        public bool IsLocked
            => _marksSeen >= LockMarks && _nowMs - _lastReadableMs <= _opt.LockHoldMs;

        /// <summary>
        /// Whether the recent marks can be Morse at all. See
        /// <see cref="CwReadability"/> - this is not the same question as
        /// <c>Confidence</c>, and on a chattering detector the two disagree.
        /// </summary>
        public CwReadability Readability => AssessReadability(out _);

        /// <summary>
        /// Whether <see cref="Readability"/> is answering the question at all.
        ///
        /// <see cref="CwReadability.Unknown"/> covers two states that read the
        /// same and mean opposite things: "not enough marks have arrived to
        /// judge yet", which is where every transmission starts, and "the marks
        /// that were being judged have gone stale", which is silence. The
        /// window counts rise to capacity and never fall, so the count itself
        /// separates them.
        ///
        /// CwDecoderEngine.Gate needs that separation. It holds text until the
        /// marks prove readable and discards the hold once it has gone stale,
        /// and it was measuring stale from the first frame - so on a Farnsworth
        /// sender the discard fired before the evidence had had time to arrive.
        /// At 5 WPM the tenth mark lands about 8.4 s in and the hold expired at
        /// 5 s, which cost "CQ " off the front of the message: the opening,
        /// which on air is the callsign.
        /// </summary>
        public bool HasReadabilityVerdict => _markWindowCount >= _opt.ReadabilityMinMarks;

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
            _nowMs = tMs;

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
                //
                // Flushing early is about latency - getting the character on
                // screen rather than holding it until the operator sends again -
                // so it must not disturb the measurement of the gap it happens
                // in. Moving _edgeTimeMs here to stop a repeat flush did disturb
                // it, and expensively: a Farnsworth sender at 5 WPM leaves 1498
                // ms between characters, a hair under this 1500 ms, so the flush
                // fired inside the gaps and OnGap was handed what was left of
                // them instead of their real length. Every word gap in the ARRL
                // 5 WPM file then measured short of the threshold and no space
                // was emitted anywhere: 381 characters, all of them correct, run
                // together as "THELETTERKMEANSTHATSTHEENDOFMYMESSAGE". Leaving
                // the edge alone and latching a flag instead scores 97.1% where
                // that scored 79.0%.
                if (!_keyDown && _symbol.Length > 0 && !_idleFlushed
                    && tMs - _edgeTimeMs >= _opt.IdleFlushMs)
                {
                    _idleFlushed = true;
                    return FlushCharacter();
                }
                return string.Empty;
            }

            double durationMs = tMs - _edgeTimeMs;
            _edgeTimeMs = tMs;
            _idleFlushed = false;
            ForgetStaleEvidence();
            bool wasKeyDown = _keyDown;
            _keyDown = sample.KeyDown;

            // De-glitch. A run too short to be an element is noise; undo the edge
            // so the two runs either side of it join up.
            //
            // "Too short" has to be read against the speed being sent, not as a
            // constant. A fixed 12 ms floor is a fifth of a dit at 20 WPM and a
            // seventh of one at 14.6, which leaves plenty of room for a noise
            // blip to pass as an element - and an element that should not be
            // there is more expensive than a missing one, because it corrupts
            // the symbol rather than shortening it, MorseTable.Decode returns
            // null and the whole character disappears with no mark on the page.
            // That is the shape of the low-SNR failure measured on 2026-08-27:
            // going from 6 dB to 3 dB in 500 Hz, the 20 WPM file gained 157
            // marks and lost 192 characters, with p10 of the mark lengths
            // falling from 60 ms to 55 while p90 stayed at 180 - dits
            // fragmenting and dahs untouched.
            //
            // Scaling with the tracked dit only bites where there is headroom.
            // At 40 WPM a dit is 30 ms and this asks for 10, under the floor, so
            // fast sending is left exactly as it was; at 14.6 WPM it asks for 29
            // and throws out blips a fixed floor waved through.
            double minElementMs = Math.Max(_opt.MinElementMs, MinElementDits * _ditMs);
            if (durationMs < minElementMs)
            {
                // The blip is not an element, but it is still evidence, and
                // throwing it away silently launders the channel: the de-glitch
                // removes exactly the short outliers the readability spread is
                // measured from, so the copy improves and the warning that the
                // copy is bad disappears with it. It is counted as a dirty run
                // rather than fed back into the spread - see
                // ReadabilityDirtyCeiling for why the obvious version is wrong.
                if (wasKeyDown) RecordRun(tooShort: true);

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
            RecordForReadability(markMs);
            RecordRun(tooShort: false);

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

            // Refresh the lock here rather than on every 5 ms hop: the answer
            // can only change when a mark arrives, and this sorts the window.
            var verdict = AssessReadability(out _);
            if (_marksSeen >= LockMarks && verdict == CwReadability.Readable)
                _lastReadableMs = _nowMs;

            // Jumbled cancels the hold outright instead of waiting it out.
            // The hold is there so a fade does not blink the lock off, and a
            // fade shows up as Chatter or as marks simply stopping - absence
            // of evidence, which is what a hold is for. Jumbled is the other
            // thing: marks are still arriving and they are actively not Morse.
            // Waiting that out is how bench/diag-dead.wav kept saying locked at
            // 58.6 wpm for six seconds after the elements test had already
            // condemned it, on a band with nothing on it. Real recordings spend
            // 0-3% of their marks Jumbled, so this costs them nothing.
            if (verdict == CwReadability.Jumbled)
                _lastReadableMs = double.NegativeInfinity;

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

        /// <summary>
        /// A gap under <see cref="CwElementDecoderOptions.CharacterGapDits"/>
        /// dits is inside a character. Past that it separates
        /// either two characters or two words, and which one is a question the
        /// element dit cannot answer.
        ///
        /// The rule here used to be "a word gap is five dits or more", from the
        /// textbook 1 / 3 / 7 timing. That is only true when the sender uses
        /// textbook timing. Under Farnsworth - characters sent at a readable
        /// speed and the gaps stretched to bring the overall rate down - the
        /// two scales come apart, and every practice recording below about
        /// 15 WPM is sent that way. All ten ARRL W1AW files send their elements
        /// at the same 14.6 WPM, an 85 ms dit, and vary only the gaps:
        ///
        ///     file      character gap   word gap
        ///      5 WPM        1498 ms      5506 ms
        ///     10 WPM         552 ms      1293 ms
        ///     13 WPM         333 ms       783 ms
        ///
        /// Five dits is 425 ms, so on all three every gap between characters
        /// cleared the bar. The decoder had the Morse right and printed
        /// "2 0 2 4  Q S T", a space wedged between every letter, which reads
        /// as broken even though nothing was misread.
        ///
        /// So the character gap is measured rather than derived, as a low
        /// percentile of the gaps recently seen. English runs about four
        /// characters to a word, so the great majority of separators are
        /// character gaps and the lower quartile lands inside that cluster
        /// wherever it happens to sit.
        ///
        /// A percentile rather than the tracked pair of centroids that marks
        /// use, because centroids have a wrong answer they will not leave. Seed
        /// them from the element dit, feed them Farnsworth gaps, and every gap
        /// starts out above the split; the pull toward 7:3 then holds the
        /// character estimate at three sevenths of the word estimate, which
        /// keeps the split above the character gaps forever. Measured on the
        /// 10 WPM file that fixed point is exactly what happened, and the score
        /// did not move.
        /// </summary>
        private string OnGap(double gapMs)
        {
            if (gapMs < _opt.CharacterGapDits * _ditMs) return string.Empty;

            var text = FlushCharacter();

            _gapWindow[_gapWindowNext] = gapMs;
            _gapWindowNext = (_gapWindowNext + 1) % _gapWindow.Length;
            if (_gapWindowCount < _gapWindow.Length) _gapWindowCount++;

            if (gapMs >= WordGapMs() && !_pendingCharGap)
            {
                text += " ";
                _pendingCharGap = true;
            }

            return text;
        }

        /// <summary>
        /// The lower quartile of the separator gaps seen lately, floored at the
        /// textbook three dits so it can be stretched but never squeezed.
        ///
        /// The window is used from the very first gap rather than waiting for
        /// enough of them to make a respectable percentile, and that is
        /// deliberate. The caller records the gap before asking about it, so on
        /// the first one the estimate is that gap itself and the ratio test
        /// cannot fire: the decoder declines to split until it has seen a
        /// second, longer gap to compare against. Falling back to the textbook
        /// three dits instead - the obvious reading of "not enough data yet" -
        /// picks the one answer that is definitely wrong on a Farnsworth
        /// sender, and picks it for the opening of the transmission, which is
        /// the callsign. Waiting for eight gaps turned "CQ CQ DE MM5AGM" into
        /// "C Q D E MM5AGM": every character correct and the first third of the
        /// message split into letters.
        ///
        /// Erring towards not splitting is the cheap direction. A missed word
        /// gap costs one edit once per word; a wrongly split one costs an edit
        /// on every character, and reads as gibberish rather than as running
        /// text.
        ///
        /// This said "from the very first gap" while the code still waited for
        /// six, and the six were enough to do the same damage on a smaller
        /// scale: measured over the Morse Code Ninja corpus on 2026-09-01, the
        /// first entry of a Farnsworth recording came out "N 7 M O" and the
        /// second, identical sending came out "N7MO" - the split stopped as
        /// soon as the window filled. Straight-timed recordings never showed
        /// it. Now the guard is only the empty window.
        /// </summary>
        private double CharacterGapMs()
        {
            double textbook = 3.0 * _ditMs;
            if (_gapWindowCount == 0) return textbook;

            Span<double> sorted = stackalloc double[_gapWindowCount];
            for (int i = 0; i < _gapWindowCount; i++) sorted[i] = _gapWindow[i];
            sorted.Sort();

            return Math.Max(textbook, sorted[(int)(0.25 * (_gapWindowCount - 1))]);
        }

        /// <summary>
        /// Where to split character gaps from word gaps, measured rather than
        /// assumed.
        ///
        /// <see cref="WordGapRatio"/> times the character gap is a model: it
        /// says that whatever an operator does to their character gaps, they do
        /// proportionally to their word gaps. Real fists do not oblige.
        /// Measured on `bench/mkii-i1yrl.wav` on 2026-09-01 - the only plain-QSO
        /// recording in the corpus - the character gaps centre on 4.4 dits and
        /// the word gaps on 5.8, a ratio of 1.3. The operator had stretched his
        /// character gaps and left his word gaps alone. Scaling from the one to
        /// the other put the split at 6.7 dits, above two thirds of his word
        /// gaps, and the reader printed TKSFORINFOAND, QTHNRTIN and CQCQCQDE.
        ///
        /// So the two populations are separated instead of derived from each
        /// other: two-means on the log of the recent separator gaps, seeded
        /// where the ratio rule would have put them, and the split taken as the
        /// geometric mean of the two centres it settles on. Logs because the
        /// quantity is a ratio - the distance from 3 dits to 4 is the same kind
        /// of distance as from 6 to 8 - and because it stops one long pause
        /// dominating the arithmetic.
        ///
        /// Three guards, each of which costs a real measurement to remove:
        ///
        /// - Gaps over <see cref="WordGapClipDits"/> character gaps are clipped
        ///   before clustering. A sender who stops to listen leaves a gap of
        ///   tens of dits, and one of those drags the upper centre up far
        ///   enough to swallow every genuine word gap under it.
        /// - If the two centres end up closer than
        ///   <see cref="WordGapMinSeparation"/>, there is one population and not
        ///   two - the sender is running everything together, or has not sent a
        ///   word gap yet - and the ratio rule is used unchanged. Inventing a
        ///   split inside a single cluster is how you get a space between every
        ///   letter.
        /// - The answer is clamped so it can never sit at or below the measured
        ///   character gap, and never above where the ratio rule would have put
        ///   it. The lower bound is what stops a bad cluster splitting every
        ///   character; the upper bound makes the whole change one-directional,
        ///   so it can only add word gaps that were being missed and can never
        ///   remove one that is being found today.
        ///
        /// The asymmetry in that last guard is deliberate and is the same one
        /// <see cref="CharacterGapMs"/> argues for: a missed word gap costs one
        /// edit per word, a spurious one costs an edit per character.
        /// </summary>
        private double WordGapMs()
        {
            double charGap = CharacterGapMs();
            double ratio = WordGapRatio * charGap;
            if (_gapWindowCount < WordGapMinGaps) return ratio;

            double clip = WordGapClipDits * charGap;
            Span<double> logs = stackalloc double[_gapWindowCount];
            for (int i = 0; i < _gapWindowCount; i++)
                logs[i] = Math.Log(Math.Min(_gapWindow[i], clip));

            double lo = Math.Log(charGap);
            double hi = Math.Log(ratio);

            for (int pass = 0; pass < WordGapPasses; pass++)
            {
                double sumLo = 0.0, sumHi = 0.0;
                int nLo = 0, nHi = 0;
                double mid = 0.5 * (lo + hi);

                foreach (double v in logs)
                {
                    if (v < mid) { sumLo += v; nLo++; }
                    else { sumHi += v; nHi++; }
                }

                // An empty side means the seed was outside the data entirely.
                // There is nothing to separate, so leave the model alone.
                if (nLo == 0 || nHi == 0) return ratio;

                double newLo = sumLo / nLo;
                double newHi = sumHi / nHi;
                bool settled = Math.Abs(newLo - lo) < 1e-4 && Math.Abs(newHi - hi) < 1e-4;
                lo = newLo;
                hi = newHi;
                if (settled) break;
            }

            if (hi - lo < Math.Log(WordGapMinSeparation)) return ratio;

            // How much of the window ended up in the upper cluster. English
            // runs about four and a half characters to a word, so roughly one
            // separator in five is a word gap; a "word" cluster holding half
            // the separators is not one. This is what keeps noise out. Under
            // fading and hiss a real gap gets broken in two by a spurious mark,
            // and the fragments pile up into a second population that clusters
            // beautifully and means nothing - measured on the 5 WPM ARRL file
            // at 3 dB SNR, where believing it cost 13 points.
            double boundary = 0.5 * (lo + hi);
            int upper = 0;
            foreach (double v in logs) if (v >= boundary) upper++;
            double share = (double)upper / _gapWindowCount;
            if (share < WordGapMinShare || share > WordGapMaxShare) return ratio;

            double split = Math.Exp(boundary);
            return Math.Clamp(split, WordGapFloor * charGap, ratio);
        }

        private string FlushCharacter()
        {
            if (_symbol.Length == 0) return string.Empty;

            string sym = _symbol.ToString();
            _symbol.Clear();

            var decoded = MorseTable.Decode(sym);
            if (decoded is null) { _unknownSymbols++; return string.Empty; }

            _ditOnly[_ditOnlyNext]     = sym.IndexOf('-') < 0;
            _charElements[_ditOnlyNext] = (byte)Math.Min(sym.Length, 255);
            _lastDitOnlyAtMs = _nowMs;
            _ditOnlyNext = (_ditOnlyNext + 1) % _ditOnly.Length;
            if (_ditOnlyCount < _ditOnly.Length) _ditOnlyCount++;

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

        private void RecordForReadability(double markMs)
        {
            _markWindow[_markWindowNext] = markMs;
            _lastMarkAtMs = _nowMs;
            _markWindowNext = (_markWindowNext + 1) % _markWindow.Length;
            if (_markWindowCount < _markWindow.Length) _markWindowCount++;
        }

        /// <summary>
        /// Empty the evidence windows when a transmission begins after a
        /// silence long enough that what is in them cannot describe it.
        ///
        /// These are rings whose counts rise to capacity and never fall, so
        /// without this a verdict outlives the marks behind it - and worse, it
        /// outlives them as a verdict rather than as an absence. The two are
        /// different to every caller: "these marks are not Morse" is a finding,
        /// while "no marks yet" is the state every transmission opens in, and
        /// the second one must not inherit the first one's consequences. With
        /// the windows left full, a station arriving after a quiet band was
        /// judged on the previous station's marks and lost its first character
        /// to the hold being dropped underneath it.
        ///
        /// The gap window goes too, and for the same reason with a sharper
        /// edge: it holds the measured character gap, which is a property of
        /// one operator's fist. Carrying a Farnsworth beginner's stretched gaps
        /// into the next station to come up on frequency puts the word split in
        /// the wrong place for as long as it takes the percentile to migrate.
        ///
        /// The horizon is <see cref="CwElementDecoderOptions.ReadabilityMaxAgeSeconds"/>
        /// - the same silence that already withdraws a Readable verdict, so
        /// there is one definition of "gone quiet" rather than two.
        ///
        /// It fires on the first edge after the silence rather than at the
        /// moment the silence passes the horizon, and that timing is the whole
        /// of it. Clearing eagerly answers "no idea" for a band that has just
        /// been judged and found to be chattering, which is the one verdict an
        /// operator most needs to keep seeing; clearing on arrival keeps that
        /// verdict standing until there is something new to replace it.
        /// </summary>
        private void ForgetStaleEvidence()
        {
            // Quiet means no key-down runs at all, not no usable marks. A
            // detector chattering across its hysteresis produces runs that are
            // all discarded as too short, so the accepted-mark clock stops
            // dead while the band is anything but quiet - and the run window
            // recording that chatter is the only evidence of it.
            double lastActivityMs = Math.Max(_lastMarkAtMs, _lastRunAtMs);
            if (_nowMs - lastActivityMs <= _opt.ReadabilityMaxAgeSeconds * 1000.0) return;
            if (_markWindowCount == 0 && _runWindowCount == 0
                && _ditOnlyCount == 0 && _gapWindowCount == 0) return;

            _markWindowCount = _markWindowNext = 0;
            _runWindowCount  = _runWindowNext  = 0;
            _ditOnlyCount    = _ditOnlyNext    = 0;
            _gapWindowCount  = _gapWindowNext  = 0;
        }

        /// <summary>Every key-down run, whether or not it survived the de-glitch.</summary>
        private void RecordRun(bool tooShort)
        {
            _runWindow[_runWindowNext] = tooShort;
            _lastRunAtMs = _nowMs;
            _runWindowNext = (_runWindowNext + 1) % _runWindow.Length;
            if (_runWindowCount < _runWindow.Length) _runWindowCount++;
        }

        /// <summary>
        /// Sort the recent marks and compare the 90th percentile with the
        /// 10th. Percentiles rather than min and max because a single glitch
        /// at either end would otherwise decide the answer, and the whole
        /// point is to characterise the population.
        ///
        /// The spread is then overruled by the share of runs the de-glitch
        /// threw away, which the spread cannot see by construction.
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

            if (_runWindowCount >= _opt.ReadabilityMinMarks)
            {
                int dirty = 0;
                for (int i = 0; i < _runWindowCount; i++) if (_runWindow[i]) dirty++;
                if ((double)dirty / _runWindowCount > _opt.ReadabilityDirtyCeiling)
                    return CwReadability.Jumbled;
            }

            if (_ditOnlyCount >= _opt.DitOnlyMinChars && _nowMs - _lastDitOnlyAtMs <= _opt.DitOnlyMaxAgeSeconds * 1000.0)
            {
                int ditOnly = 0;
                for (int i = 0; i < _ditOnlyCount; i++) if (_ditOnly[i]) ditOnly++;
                if ((double)ditOnly / _ditOnlyCount > _opt.ReadabilityDitOnlyCeiling)
                    return CwReadability.Jumbled;
            }

            if (_ditOnlyCount >= _opt.MinElementsPerCharMinChars
                && _nowMs - _lastDitOnlyAtMs <= _opt.DitOnlyMaxAgeSeconds * 1000.0)
            {
                int elements = 0;
                for (int i = 0; i < _ditOnlyCount; i++) elements += _charElements[i];
                if ((double)elements / _ditOnlyCount < _opt.ReadabilityMinElementsPerChar)
                    return CwReadability.Jumbled;
            }

            // Only the Readable verdict is withdrawn when the evidence goes
            // stale, and deliberately only that one.
            //
            // These windows are rings whose counts rise to capacity and never
            // fall, so a verdict outlives the marks behind it. That is harmless
            // for Chatter and Jumbled - they say "do not trust this", and
            // saying it a while longer costs nothing. It is not harmless for
            // Readable: CwDecoderEngine.Gate releases held text on exactly that
            // verdict, so a latched Readable turns the silence after a station
            // into copy. Measured on air 2026-08-29 - a station read Readable
            // for minutes after it stopped sending, and noise dits reached the
            // panel as text.
            //
            // The test is silence - how long since a mark was last recorded -
            // and not the age of each mark. Per-entry ageing looked equivalent
            // and was not: it broke Farnsworth, where 5 wpm spreads ten marks
            // over more than ten seconds on a signal being copied perfectly,
            // and it answered "no idea" for a blip stream whose marks are all
            // discarded before they reach this window.
            if (_nowMs - _lastMarkAtMs > _opt.ReadabilityMaxAgeSeconds * 1000.0)
                return CwReadability.Unknown;

            return CwReadability.Readable;
        }

        private void Clamp()
        {
            _ditMs = Math.Clamp(_ditMs, _minDitMs, _maxDitMs);
            _dahMs = Math.Clamp(_dahMs, _minDitMs * 2.0, _maxDitMs * 4.0);
        }
    }
}
