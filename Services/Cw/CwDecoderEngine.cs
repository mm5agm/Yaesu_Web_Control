using System.Text;

namespace RadioWebControl.Core.Services.Cw
{
    public sealed class CwDecoderOptions
    {
        /// <summary>Rate of the frames handed in. Must be a whole multiple of 8000.</summary>
        public int InputSampleRate { get; set; } = 48000;

        /// <summary>The configured CW pitch, Hz. Centre of the tone search.</summary>
        public double PitchHz { get; set; } = 600.0;

        /// <summary>Half-width of the tone search, Hz.</summary>
        public double SearchWindowHz { get; set; } = 250.0;

        /// <summary>Narrowest search half-width worth using, Hz.</summary>
        public const double MinSearchWindowHz = 100.0;

        /// <summary>Widest search half-width worth using, Hz.</summary>
        public const double MaxSearchWindowHz = 1800.0;

        /// <summary>
        /// The tone search half-width the radio's own IF filter implies. A
        /// signal the operator cannot hear is not one to hunt for, and what
        /// they can hear is the passband: half the filter width either side of
        /// the pitch covers exactly that and nothing beyond it.
        ///
        /// Measured 2026-08-26 on bench/sp5xoc.wav, a station 232 Hz off the
        /// 610 Hz pitch inside a 500 Hz filter. At the implied 250 Hz the
        /// decoder finds it and prints "FM 5NN TU ... CQCQDE"; at 150 it
        /// transcribes the noise beside it; at 300 it reaches past the skirt
        /// and starts chasing energy the filter has already thrown away.
        ///
        /// Clamped at the bottom, where the narrowest CW filters leave a window
        /// smaller than the error in the tone estimate itself.
        ///
        /// The top clamp used to be 500, on the reasoning that a 3.6 kHz SSB
        /// filter would otherwise licence a lock 1.8 kHz off the pitch, which
        /// is a different QSO rather than a mistuned one. That reasoning was
        /// sound and the consequence was still wrong. On 2026-08-27 a station
        /// on 40 m sat at 1640 Hz in the audio with a 3.2 kHz filter open -
        /// loud, clean, and plainly audible - and the reader never saw it,
        /// because 500 pinned the search to 450-950 Hz. An operator who can
        /// hear a signal and watches the reader ignore it is being told the
        /// decoder is broken, and from where they sit that is a fair reading.
        ///
        /// So the window now follows the filter out to a full SSB passband,
        /// and the "different QSO" problem is answered where it actually
        /// arises instead: CwToneDetector acquires across this whole window
        /// but, once it has a confident lock, tracks within a narrow band of
        /// the tone it found. A louder neighbour cannot steal an established
        /// lock, and nothing audible is invisible any more.
        /// </summary>
        public static double SearchWindowForFilterWidth(int filterWidthHz)
            => Math.Clamp(filterWidthHz / 2.0, MinSearchWindowHz, MaxSearchWindowHz);

        /// <summary>
        /// False pins the detector to PitchHz instead of hunting for the tone.
        /// Reader Mode wants this: the operator has already tuned the signal in,
        /// so hunting can only find the wrong one.
        /// </summary>
        public bool TrackPitch { get; set; } = true;

        /// <summary>Detector settling time before anything is reported as keyed, seconds.</summary>
        public double WarmupSeconds { get; set; } = new CwToneDetectorOptions().WarmupSeconds;

        /// <summary>Erase key-state runs shorter than this, ms. 0 disables it.</summary>
        public double KeyDebounceMs { get; set; } = new CwToneDetectorOptions().KeyDebounceMs;

        /// <summary>
        /// How long the marks may stay unreadable before text held from before
        /// the bad patch is thrown away rather than kept waiting, seconds.
        ///
        /// Held text is released when the signal proves readable again, which
        /// is what carries a station across a fade. Without a bound that same
        /// mechanism dumps a bufferful of chatter the moment a marginal signal
        /// manages one readable window. Measured on bench/probe15c.wav - a
        /// signal the operator described as "in and out" - an unbounded hold
        /// leaked 247 characters where the bound lets through far less.
        ///
        /// It must comfortably exceed a deep fade, or it undoes the point of
        /// holding at all.
        /// </summary>
        public double HoldStaleSeconds { get; set; } = 5.0;

        public CwElementDecoderOptions Element { get; set; } = new();
    }

    /// <summary>
    /// The decoder, assembled: audio in, characters out.
    ///
    /// It owns a <see cref="CwToneDetector"/> (tone tracking and an adaptive
    /// key-down decision) and a <see cref="CwElementDecoder"/> (adaptive dit/dah
    /// timing), and does nothing itself beyond joining them and keeping the
    /// readouts an application needs to show.
    ///
    /// Radio-agnostic by construction: it never learns what is on the other end
    /// of the audio, which is what lets the test suite drive it with generated
    /// Morse and both applications drive it from their own capture stacks.
    /// </summary>
    public sealed class CwDecoderEngine
    {
        private readonly CwDecoderOptions _opt;
        private readonly CwToneDetector   _tone;
        private readonly CwElementDecoder _elements;
        private readonly List<CwToneSample> _samples = new(256);
        private readonly object _gate = new();

        private ICwAudioSource? _source;
        private bool   _signalPresent;
        private double _snrDb;

        // Text decoded before the readability test has enough marks to judge.
        // Held rather than emitted, so that a signal which turns out to be
        // readable does not lose its opening characters to the wait, and one
        // which turns out to be chatter never shows them at all.
        private readonly StringBuilder _pending = new();
        private const int PendingCap = 64;
        private double _badSinceSeconds = double.NaN;
        private double _nowSeconds;

        public CwDecoderEngine(CwDecoderOptions? options = null)
        {
            _opt = options ?? new CwDecoderOptions();
            _tone = new CwToneDetector(new CwToneDetectorOptions
            {
                InputSampleRate = _opt.InputSampleRate,
                PitchHz         = _opt.PitchHz,
                SearchWindowHz  = _opt.SearchWindowHz,
                TrackPitch      = _opt.TrackPitch,
                WarmupSeconds   = _opt.WarmupSeconds,
                KeyDebounceMs   = _opt.KeyDebounceMs,
            });
            _elements = new CwElementDecoder(_opt.Element);
        }

        /// <summary>Raised for each run of newly decoded characters.</summary>
        public event Action<string>? TextDecoded;

        public double WordsPerMinute => _elements.WordsPerMinute;
        public double ToneHz         => _tone.ToneHz;
        public double Confidence     => _tone.Confidence;
        public double SnrDb          => _snrDb;
        public bool   SignalPresent  => _signalPresent;
        public bool   IsLocked       => _elements.IsLocked;

        /// <summary>
        /// Whether what is arriving can be Morse at all. Applications should
        /// show <see cref="CwReadability.Chatter"/> and
        /// <see cref="CwReadability.Jumbled"/> to the operator as "nothing
        /// readable here" rather than printing the text, which is why no text
        /// is emitted in those states.
        /// </summary>
        public CwReadability Readability => _elements.Readability;

        /// <summary>p90/p10 of recent mark lengths. Near 3 on readable Morse.</summary>
        public double MarkSpread => _elements.MarkSpread;

        /// <summary>
        /// The VFO offset that would put the tone we are hearing onto the pitch we
        /// want to hear, or null when the measurement is not worth acting on.
        /// The FTdx101 has its own ZI command and does not need this; the
        /// IC-7300 MkII has no equivalent, so Icom Web Control does.
        /// </summary>
        public long? ZeroInOffsetHz(bool lowerSideband = false)
            => CwZeroIn.ComputeOffsetWholeHz(_tone.ToneHz, _opt.PitchHz, lowerSideband,
                                             confidence: _tone.Confidence);

        /// <summary>
        /// Feed one frame of mono float audio. Returns the text it completed,
        /// which is usually empty, and raises TextDecoded when it is not.
        /// </summary>
        public string ProcessFrame(ReadOnlySpan<float> samples)
        {
            string text;

            lock (_gate)
            {
                _samples.Clear();
                _tone.Process(samples, _samples);
                if (_samples.Count == 0) return string.Empty;

                var sb = new StringBuilder();
                foreach (var s in _samples)
                {
                    var produced = _elements.Push(s);
                    if (produced.Length > 0) sb.Append(produced);
                }

                var last = _samples[^1];
                _snrDb         = last.SnrDb;
                _signalPresent = last.SignalPresent;
                _nowSeconds    = last.TimeSeconds;

                text = Gate(sb.ToString());
            }

            if (text.Length > 0) TextDecoded?.Invoke(text);
            return text;
        }

        /// <summary>
        /// Decide what of the freshly decoded text may be shown.
        ///
        /// The rule is that text becomes visible only once the marks behind it
        /// have shown they can be Morse. Anything decoded before that is held,
        /// not dropped, and released the moment the signal proves readable -
        /// which is what stops a station losing its callsign to the wait, and
        /// what carries a fading signal across the fade rather than throwing
        /// away the characters either side of it.
        ///
        /// Discarding on the first bad assessment was the obvious
        /// implementation and it was wrong: a real signal dips through Chatter
        /// on any deep fade, and wiping the buffer there cost the QSB tests
        /// most of their text - 4.2% on a 20 dB fade at 0.25 Hz, against a
        /// 40% floor. Holding costs nothing, because held text that never
        /// becomes readable is never shown.
        /// </summary>
        private string Gate(string produced)
        {
            if (_elements.Readability == CwReadability.Readable)
            {
                _badSinceSeconds = double.NaN;
                if (_pending.Length == 0) return produced;
                _pending.Append(produced);
                var released = _pending.ToString();
                _pending.Clear();
                return released;
            }

            if (double.IsNaN(_badSinceSeconds)) _badSinceSeconds = _nowSeconds;
            else if (_nowSeconds - _badSinceSeconds > _opt.HoldStaleSeconds) _pending.Clear();

            _pending.Append(produced);
            if (_pending.Length > PendingCap)
                _pending.Remove(0, _pending.Length - PendingCap);
            return string.Empty;
        }

        /// <summary>
        /// Emit any part-built character. Call when capture stops, so the last
        /// letter of the last over is not silently dropped.
        ///
        /// Held text is released here only if the signal was readable: a
        /// capture that stops while the decoder is still undecided has, by
        /// definition, never shown that it was copying anything.
        /// </summary>
        public string Flush()
        {
            string text;
            lock (_gate)
            {
                text = Gate(_elements.Flush());
                _pending.Clear();
            }
            if (text.Length > 0) TextDecoded?.Invoke(text);
            return text;
        }

        /// <summary>
        /// Subscribe to an audio source. The applications own the device; Core
        /// only ever sees the frames.
        /// </summary>
        public void Attach(ICwAudioSource source)
        {
            Detach();
            _source = source;
            _source.FrameAvailable += OnFrame;
        }

        public void Detach()
        {
            if (_source is null) return;
            _source.FrameAvailable -= OnFrame;
            _source = null;
        }

        private void OnFrame(ReadOnlyMemory<float> frame) => ProcessFrame(frame.Span);
    }
}
