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

        /// <summary>
        /// False pins the detector to PitchHz instead of hunting for the tone.
        /// Reader Mode wants this: the operator has already tuned the signal in,
        /// so hunting can only find the wrong one.
        /// </summary>
        public bool TrackPitch { get; set; } = true;

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

        public CwDecoderEngine(CwDecoderOptions? options = null)
        {
            _opt = options ?? new CwDecoderOptions();
            _tone = new CwToneDetector(new CwToneDetectorOptions
            {
                InputSampleRate = _opt.InputSampleRate,
                PitchHz         = _opt.PitchHz,
                SearchWindowHz  = _opt.SearchWindowHz,
                TrackPitch      = _opt.TrackPitch,
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

                text = sb.ToString();
            }

            if (text.Length > 0) TextDecoded?.Invoke(text);
            return text;
        }

        /// <summary>
        /// Emit any part-built character. Call when capture stops, so the last
        /// letter of the last over is not silently dropped.
        /// </summary>
        public string Flush()
        {
            string text;
            lock (_gate) text = _elements.Flush();
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
