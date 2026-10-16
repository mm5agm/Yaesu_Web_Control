using System;
using System.Collections.Generic;
using System.Linq;

namespace RadioWebControl.Core.Services.Rtty
{
    /// <summary>
    /// A stretch of one tone: which one, and how long in bit times.
    ///
    /// A run-length stream rather than a bit per element because the stop
    /// element is traditionally one and a half bits long, and nothing else
    /// expresses that honestly. It is also the shape a keyer wants - hold this
    /// line in this state for this long - so the same stream drives true FSK
    /// and audio alike.
    /// </summary>
    public readonly record struct RttyPulse(bool Mark, double Bits);

    /// <summary>
    /// Text to keying: the transmit half of RTTY, down to either a line to
    /// toggle or a block of audio.
    ///
    /// <para><b>Nothing transmits yet.</b> This exists because the test suite
    /// needs to make RTTY to decode - a decoder tested only against the air is
    /// a decoder tested against an unknown - and because that generator and a
    /// transmitter are the same object. Writing it in Core, rather than as a
    /// fixture inside the test project, is what makes adding a transmitter
    /// later a wiring job.</para>
    ///
    /// <para>There are two ways an IC-7300 can be made to send RTTY and this
    /// serves both. <see cref="Pulses"/> is the stream for <b>true FSK</b>,
    /// where the radio makes the tones and the application keys a control line
    /// (SET &gt; Connectors &gt; USB Keying (RTTY), CI-V 1A 05 00 98, points
    /// that at a DTR or RTS line). <see cref="ToAudio"/> is the same stream as
    /// <b>AFSK</b>, two audio tones to feed the radio's input. Neither is
    /// wired up; both start here.</para>
    /// </summary>
    public static class RttyModulator
    {
        /// <summary>The usual amateur rate. 22 ms a bit.</summary>
        public const double Baud45 = 45.45;

        /// <summary>What most European commercial and utility stations use, DDK9 among them.</summary>
        public const double Baud50 = 50.0;

        /// <summary>
        /// The frame around each character: a space-polarity start element, the
        /// five data bits lowest first, then a mark-polarity stop element one
        /// and a half bits long.
        ///
        /// Mark is the idle state, so a line that is doing nothing sits at mark
        /// and the falling edge into the start element is what a receiver looks
        /// for.
        /// </summary>
        /// <param name="codes">Five-bit codes, as <see cref="Ita2Codec.Encode"/> produces.</param>
        /// <param name="stopBits">
        /// Length of the stop element. 1.5 is the mechanical standard; 1.0 and
        /// 2.0 are both met on the air, and a receiver does not much care
        /// because it resynchronises on every start edge.
        /// </param>
        /// <param name="idleBitsBefore">
        /// Mark held before the first character. A receiver needs to see idle
        /// to know where the first falling edge is, and a transmitter needs it
        /// to let the far end's AGC settle.
        /// </param>
        /// <param name="idleBitsAfter">Mark held after the last character.</param>
        public static IEnumerable<RttyPulse> Pulses(
            IReadOnlyList<byte> codes,
            double stopBits = 1.5,
            double idleBitsBefore = 0,
            double idleBitsAfter = 0)
        {
            if (codes is null) throw new ArgumentNullException(nameof(codes));
            if (stopBits <= 0) throw new ArgumentOutOfRangeException(nameof(stopBits));

            if (idleBitsBefore > 0) yield return new RttyPulse(true, idleBitsBefore);

            foreach (var code in codes)
            {
                if (code > 31) throw new ArgumentOutOfRangeException(nameof(codes), $"code {code} is not five bits");

                yield return new RttyPulse(false, 1.0);           // start

                for (int bit = 0; bit < 5; bit++)                  // data, lowest bit first
                    yield return new RttyPulse((code & (1 << bit)) != 0, 1.0);

                yield return new RttyPulse(true, stopBits);        // stop
            }

            if (idleBitsAfter > 0) yield return new RttyPulse(true, idleBitsAfter);
        }

        /// <summary>Text straight to keying, in one step.</summary>
        public static IEnumerable<RttyPulse> Pulses(
            string text,
            RttyFigureSet set = RttyFigureSet.Ita2,
            bool usos = true,
            double stopBits = 1.5,
            double idleBitsBefore = 0,
            double idleBitsAfter = 0,
            bool startInLetters = true)
            => Pulses(Ita2Codec.Encode(text, set, usos, startInLetters),
                      stopBits, idleBitsBefore, idleBitsAfter);

        /// <summary>
        /// The same keying as two audio tones.
        ///
        /// <para><b>The phase is continuous across every change of tone.</b>
        /// Switching between two independent oscillators leaves a step in the
        /// waveform at each transition, and a step is broadband - it puts
        /// energy far outside the few hundred Hz the signal is supposed to
        /// occupy, which on the air is splatter across other people's
        /// contacts. Carrying one phase accumulator and only changing the rate
        /// it advances at is what makes the signal as narrow as it should be.
        /// It also matters for the receiver here: a discontinuity rings both
        /// tone filters at once and reads as a momentary wrong bit.</para>
        /// </summary>
        /// <param name="pulses">Keying, from <see cref="Pulses(string, RttyFigureSet, bool, double, double, double)"/>.</param>
        /// <param name="sampleRate">Hz.</param>
        /// <param name="markHz">Mark tone in the audio.</param>
        /// <param name="spaceHz">Space tone in the audio.</param>
        /// <param name="baud">Symbol rate: 45.45 for amateur RTTY, 50 for most European utility.</param>
        /// <param name="amplitude">Peak amplitude, 0 to 1.</param>
        public static float[] ToAudio(
            IEnumerable<RttyPulse> pulses,
            int sampleRate,
            double markHz,
            double spaceHz,
            double baud,
            double amplitude = 0.5)
        {
            if (pulses is null) throw new ArgumentNullException(nameof(pulses));
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (baud <= 0) throw new ArgumentOutOfRangeException(nameof(baud));

            var samplesPerBit = sampleRate / baud;
            var samples = new List<float>();

            double phase = 0;
            double carried = 0;   // fractional samples owed from the last pulse

            foreach (var pulse in pulses)
            {
                // The bit period is not a whole number of samples (22 ms at
                // 48 kHz is 1056.0528...), so the remainder is carried rather
                // than dropped. Dropped, it is a steady timing error that
                // accumulates over an over and eventually walks the sampling
                // instant out of the bit.
                var want = pulse.Bits * samplesPerBit + carried;
                var count = (int)Math.Round(want);
                carried = want - count;

                var step = 2 * Math.PI * (pulse.Mark ? markHz : spaceHz) / sampleRate;

                for (int i = 0; i < count; i++)
                {
                    samples.Add((float)(amplitude * Math.Sin(phase)));
                    phase += step;
                    if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
                }
            }

            return samples.ToArray();
        }

        /// <summary>Text straight to audio, for tests and for an AFSK transmitter.</summary>
        public static float[] ToAudio(
            string text,
            int sampleRate,
            double markHz,
            double spaceHz,
            double baud,
            RttyFigureSet set = RttyFigureSet.Ita2,
            bool usos = true,
            double stopBits = 1.5,
            double idleBitsBefore = 20,
            double idleBitsAfter = 10,
            double amplitude = 0.5,
            bool startInLetters = true)
            => ToAudio(
                Pulses(text, set, usos, stopBits, idleBitsBefore, idleBitsAfter, startInLetters),
                sampleRate, markHz, spaceHz, baud, amplitude);

        /// <summary>
        /// How long a keying stream lasts, in seconds. Useful for sizing a
        /// buffer, and for a transmitter that has to know when to drop PTT.
        /// </summary>
        public static double Duration(IEnumerable<RttyPulse> pulses, double baud)
            => pulses.Sum(p => p.Bits) / baud;
    }
}
