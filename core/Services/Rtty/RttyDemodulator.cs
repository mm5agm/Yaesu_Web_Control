using System;
using System.Text;

namespace RadioWebControl.Core.Services.Rtty
{
    /// <summary>
    /// Audio in, text out: the listening side of RTTY.
    ///
    /// <para>Three stages, and they are worth naming because when a RTTY decoder
    /// copies badly it is usually one of them that is wrong, and the symptom
    /// looks much the same whichever it is.</para>
    ///
    /// <para><b>1. Which tone is sounding.</b> The audio is mixed down against
    /// each tone in turn and averaged over exactly one bit time by
    /// <see cref="RttyToneMagnitude"/>, which is where the reasoning about that
    /// window length lives. The two magnitudes are subtracted and the
    /// <em>sign</em> of the difference is the decision. Subtracting rather than
    /// comparing against a level is what makes the decoder indifferent to signal
    /// strength: a station fading by 20 dB moves both magnitudes together and
    /// does not move the crossing.</para>
    ///
    /// <para><b>2. Where the character starts.</b> An idle line sits at mark, so
    /// a character announces itself by falling to space. That edge is the only
    /// timing reference there is, because RTTY carries no clock. Every character
    /// is timed from its own start edge, which is why a receiver cannot drift
    /// across a transmission - only within one character.</para>
    ///
    /// <para><b>3. What the bits say.</b> Five bits to a code, lowest first, and
    /// <see cref="Ita2Decoder"/> for the alphabet and the shift state.</para>
    ///
    /// <para><b>The one-bit averaging window delays the signal by half a bit</b>,
    /// and that delay is absorbed into the sampling arithmetic rather than
    /// corrected out. A window ending at some instant covers the bit
    /// <em>before</em> that instant, so the best moment to read a bit is the
    /// instant it ends, not its centre. Hence the sampling points at 1.5, 2.5 ...
    /// 5.5 bit times after the edge: those are bit ends. Changing them to
    /// centres, which is what they look like they should be, would read every
    /// bit half a bit early.</para>
    ///
    /// <para>Nothing here is specific to a radio or a brand. Give it the two
    /// audio frequencies and the speed; <c>RttySignalAnalyser</c> is for working
    /// those out when the operator does not know them.</para>
    /// </summary>
    public sealed class RttyDemodulator
    {
        private readonly RttyToneMagnitude _mark;
        private readonly RttyToneMagnitude _space;
        private readonly Ita2Decoder _decoder;
        private readonly double _samplesPerBit;

        private enum State { Hunting, CheckingStart, Data, Stop }

        /// <summary>
        /// How clearly space has to be winning half a bit after a falling edge
        /// for it to be taken as a start element rather than noise crossing zero.
        ///
        /// Low on purpose. Rejecting marginal bits here costs real characters on a
        /// signal worth copying, and it is not where noise is kept out - that is
        /// <see cref="Squelch"/>, which gets a dozen bit times to decide instead
        /// of half a bit.
        /// </summary>
        private const double StartElementFloor = 0.15;

        private readonly double _activityRate;

        private State _state = State.Hunting;
        private long _n;            // samples seen: the timebase for everything below
        private long _edge;         // where this character's start edge was seen
        private long _resumeAt;     // not before here - stops a character retriggering on itself
        private double _previous;   // the last difference, for edge detection
        private int _code;
        private int _bit;

        /// <param name="sampleRate">Hz.</param>
        /// <param name="markHz">The mark tone in the receive audio.</param>
        /// <param name="spaceHz">
        /// The space tone. Above mark or below it - the demodulator neither cares
        /// nor needs telling, so a station sending reversed is handled by passing
        /// the two frequencies the other way round.
        /// </param>
        /// <param name="baud">45.45 for amateur RTTY, 50 for most European utility stations.</param>
        /// <param name="set">Which figures table to print above the shift.</param>
        /// <param name="usos">Unshift on space. See <see cref="Ita2Codec"/>.</param>
        public RttyDemodulator(
            int sampleRate,
            double markHz,
            double spaceHz,
            double baud,
            RttyFigureSet set = RttyFigureSet.Ita2,
            bool usos = true)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (baud <= 0) throw new ArgumentOutOfRangeException(nameof(baud));
            if (markHz <= 0 || markHz >= sampleRate / 2.0) throw new ArgumentOutOfRangeException(nameof(markHz));
            if (spaceHz <= 0 || spaceHz >= sampleRate / 2.0) throw new ArgumentOutOfRangeException(nameof(spaceHz));

            SampleRate = sampleRate;
            MarkHz = markHz;
            SpaceHz = spaceHz;
            Baud = baud;

            _samplesPerBit = sampleRate / baud;
            var window = Math.Max(4, (int)Math.Round(_samplesPerBit));

            // Twelve bit times, not a fixed number of milliseconds. Long enough
            // that noise cannot hold a high average, short enough that the gate
            // is open before the first character of a transmission has finished -
            // and in bit times because otherwise it is not: measured in seconds,
            // a half-second attack swallowed the opening word at 75 and 100 baud
            // while behaving perfectly at 45.45.
            _activityRate = 1.0 / (_samplesPerBit * 12);

            _mark = new RttyToneMagnitude(window, markHz, sampleRate);
            _space = new RttyToneMagnitude(window, spaceHz, sampleRate);
            _decoder = new Ita2Decoder(set, usos);
        }

        public int SampleRate { get; }
        public double MarkHz { get; }
        public double SpaceHz { get; }
        public double Baud { get; }

        /// <summary>
        /// How much of a signal there has to be before anything is printed, from
        /// 0 to 1.
        ///
        /// <para>Compared against <see cref="SignalActivity"/>, a running average
        /// of how decisively one tone is beating the other. 0 prints
        /// everything, noise included, which is occasionally what a listener
        /// wants when digging at a signal that is barely there.</para>
        ///
        /// <para><b>This is deliberately not the same test as deciding a bit</b>,
        /// and separating the two is what makes the decoder both quiet when idle
        /// and sensitive when it matters. The first version here had one
        /// threshold doing both jobs and they pull in opposite directions: raised
        /// far enough to stop three seconds of hiss printing thirteen characters,
        /// it was also discarding real characters from a perfectly good signal -
        /// 88% copy at 8 dB above the noise, where there was no reason not to have
        /// all of it. Noise and signal are not actually hard to tell apart; they
        /// are only hard to tell apart <em>within one bit</em>. Over a dozen bits
        /// the difference is wide: hiss averages about a third, because two
        /// independent noise magnitudes are usually of similar size, while a real
        /// station averages near one even when individual bits are marginal. So
        /// the per-character test stays low and loses nothing, and the gate on
        /// printing is a slow average that noise cannot fake. Perfect copy
        /// improved from 8 dB to below 2 dB in a 500 Hz bandwidth on the strength
        /// of that one change.</para>
        /// </summary>
        public double Squelch { get; set; } = 0.45;

        /// <summary>
        /// How decisively one tone has been beating the other, averaged over the
        /// last dozen bit times: about 0.8 for a clean station, around a third for
        /// noise. It does not reach 1 even on a noiseless signal, because the
        /// averaging window is a bit long and so spans every tone transition,
        /// where neither tone is winning - which is why the default gate is 0.45
        /// and not something nearer the top of the range.
        ///
        /// <para>Worth exposing rather than keeping private - it is the honest
        /// answer to "is anything there?", which is what a signal-strength
        /// indicator on a reader panel wants to show, and what tells an automatic
        /// tone search whether it has found a RTTY signal or a quiet patch of
        /// band.</para>
        /// </summary>
        public double SignalActivity { get; private set; }

        /// <summary>
        /// Characters whose stop element was not mark. It should not happen, and
        /// means the timing or the tones are wrong.
        ///
        /// <para>The character is printed anyway. On a fading signal a stop error
        /// is usually one corrupt character in an otherwise readable sentence,
        /// and discarding it costs the operator more than printing it wrong does
        /// - they can see a wrong letter but they cannot see a missing one.</para>
        ///
        /// <para><b>A low count does not mean the settings are right.</b> It is
        /// tempting to read this as a tuning aid and it is a weak one: a 50 baud
        /// station decoded at 45.45 puts the stop sample about a third of a bit
        /// late, which a one-and-a-half-bit stop element absorbs completely, so
        /// the framing stays clean while the data bits are read at the wrong
        /// instants and the text is wrong. Only a grossly wrong speed, or tones
        /// in the wrong place, shows up here. The text itself is the better
        /// witness, which is why nothing is hidden on the strength of this.</para>
        /// </summary>
        public long FramingErrors { get; private set; }

        /// <summary>Characters decoded, framing errors included.</summary>
        public long Characters { get; private set; }

        /// <summary>True when the next code will be read from the figures table.</summary>
        public bool InFigures => _decoder.InFigures;

        /// <summary>
        /// Back to hunting for a start edge, in letters. For the start of a
        /// listening session, or when the operator changes the tones.
        /// </summary>
        public void Reset()
        {
            _state = State.Hunting;
            _previous = 0;
            _resumeAt = 0;
            SignalActivity = 0;
            _code = 0;
            _bit = 0;
            _mark.Reset();
            _space.Reset();
            _decoder.Reset();
        }

        /// <summary>
        /// Feed audio, get back whatever it printed - usually nothing, since a
        /// character takes 165 ms and an audio frame is a few milliseconds.
        /// </summary>
        public string Feed(ReadOnlySpan<float> samples)
        {
            StringBuilder? text = null;

            foreach (var sample in samples)
            {
                var mark = _mark.Process(sample);
                var space = _space.Process(sample);

                // Positive means mark. The normalised form is for the squelch,
                // so that comparison is about which tone wins rather than how
                // loud the station is.
                var difference = mark - space;
                var strength = difference / (mark + space + 1e-12);

                SignalActivity += (Math.Abs(strength) - SignalActivity) * _activityRate;

                _n++;

                switch (_state)
                {
                    case State.Hunting:
                        // Mark falling to space. _resumeAt keeps the character
                        // just decoded from being found again inside its own
                        // stop element.
                        if (_n >= _resumeAt && _previous > 0 && difference <= 0)
                        {
                            _edge = _n;
                            _state = State.CheckingStart;
                        }
                        break;

                    case State.CheckingStart:
                        // Half a bit on, the averaging window covers the start
                        // element itself. Space has to be properly winning, or
                        // this was noise crossing zero.
                        if (_n >= _edge + (long)(_samplesPerBit * 0.5))
                        {
                            if (-strength > StartElementFloor)
                            {
                                _state = State.Data;
                                _code = 0;
                                _bit = 0;
                            }
                            else
                            {
                                _state = State.Hunting;
                            }
                        }
                        break;

                    case State.Data:
                        // Bit ends, not bit centres - see the note above on the
                        // averaging window's half-bit delay.
                        if (_n >= _edge + (long)(_samplesPerBit * (1.5 + _bit)))
                        {
                            if (difference > 0) _code |= 1 << _bit;
                            if (++_bit == 5) _state = State.Stop;
                        }
                        break;

                    case State.Stop:
                        if (_n >= _edge + (long)(_samplesPerBit * 6.5))
                        {
                            if (difference <= 0) FramingErrors++;

                            // The shift state is tracked even while squelched, so
                            // that a station opening the gate mid-transmission is
                            // not read in the wrong table.
                            Characters++;
                            var printed = _decoder.Feed((byte)_code);
                            if (printed.Length > 0 && SignalActivity >= Squelch)
                                (text ??= new StringBuilder()).Append(printed);

                            // Three quarters of a bit past the stop sample. The
                            // next start edge cannot arrive before the stop
                            // element has run, and the shortest stop element met
                            // on the air is one bit.
                            _resumeAt = _edge + (long)(_samplesPerBit * 6.75);
                            _state = State.Hunting;
                        }
                        break;
                }

                _previous = difference;
            }

            return text?.ToString() ?? string.Empty;
        }
    }
}
