using System;
using System.Collections.Generic;
using System.Linq;
using RadioWebControl.Core.Services.Rtty;
using Xunit;

namespace RadioWebControl.Core.Tests.Rtty
{
    /// <summary>
    /// The framing and the waveform.
    ///
    /// These matter twice over. Today they are the foundation the demodulator's
    /// tests stand on - a decoder tested against a generator that frames
    /// characters wrongly would be tested against nothing. Later they are the
    /// transmitter, and a transmitter is checked by the people listening to it.
    /// </summary>
    public class RttyModulatorTests
    {
        private const int Rate = 48_000;
        private const double Mark = 2125;
        private const double Space = 2125 + 170;

        // --- framing ---------------------------------------------------------

        [Fact]
        public void A_character_is_a_space_start_five_data_bits_and_a_long_mark_stop()
        {
            // Letter E, 0b00001: one mark bit then four spaces.
            var p = RttyModulator.Pulses(new byte[] { 0b00001 }).ToList();

            Assert.Equal(7, p.Count);

            Assert.False(p[0].Mark);              // start is always space polarity
            Assert.Equal(1.0, p[0].Bits);

            Assert.True(p[1].Mark);               // bit 0, the first sent
            Assert.False(p[2].Mark);
            Assert.False(p[3].Mark);
            Assert.False(p[4].Mark);
            Assert.False(p[5].Mark);

            Assert.True(p[6].Mark);               // stop is mark
            Assert.Equal(1.5, p[6].Bits);
        }

        [Fact]
        public void The_data_bits_go_out_lowest_first()
        {
            // T is 0b10000 - the opposite end of the code from E, so a reversed
            // bit order would show up here even if E happened to look right.
            var bits = RttyModulator.Pulses(new byte[] { 0b10000 })
                                    .Skip(1).Take(5)
                                    .Select(p => p.Mark)
                                    .ToArray();

            Assert.Equal(new[] { false, false, false, false, true }, bits);
        }

        [Fact]
        public void An_idling_line_sits_at_mark()
        {
            var p = RttyModulator.Pulses(Array.Empty<byte>(), idleBitsBefore: 10, idleBitsAfter: 4).ToList();

            Assert.Equal(2, p.Count);
            Assert.All(p, x => Assert.True(x.Mark));
            Assert.Equal(10, p[0].Bits);
            Assert.Equal(4, p[1].Bits);
        }

        [Fact]
        public void A_character_is_seven_and_a_half_bit_times_long()
        {
            var seconds = RttyModulator.Duration(
                RttyModulator.Pulses(new byte[] { 0b00001 }), RttyModulator.Baud45);

            // 7.5 bits at 45.45 baud is 165 ms, the figure every RTTY
            // description quotes for a character.
            Assert.Equal(7.5 / 45.45, seconds, 6);
        }

        [Fact]
        public void A_stop_length_of_one_or_two_bits_is_honoured()
        {
            Assert.Equal(1.0, RttyModulator.Pulses(new byte[] { 0 }, stopBits: 1.0).Last().Bits);
            Assert.Equal(2.0, RttyModulator.Pulses(new byte[] { 0 }, stopBits: 2.0).Last().Bits);
        }

        [Fact]
        public void A_code_wider_than_five_bits_is_refused_rather_than_truncated()
        {
            // Silently masking it would send a different character.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => RttyModulator.Pulses(new byte[] { 32 }).ToList());
        }

        [Fact]
        public void Text_goes_through_the_codec_on_the_way_in()
        {
            var viaText = RttyModulator.Pulses("E", startInLetters: false).ToList();
            var viaCodes = RttyModulator.Pulses(new byte[] { 0b00001 }).ToList();

            Assert.Equal(viaCodes, viaText);
        }

        // --- the waveform ----------------------------------------------------

        [Fact]
        public void The_audio_lasts_as_long_as_the_keying_says_it_should()
        {
            var pulses = RttyModulator.Pulses("CQ DE MM5AGM", idleBitsBefore: 20).ToList();
            var audio = RttyModulator.ToAudio(pulses, Rate, Mark, Space, RttyModulator.Baud45);

            var expected = RttyModulator.Duration(pulses, RttyModulator.Baud45) * Rate;

            // Within a sample: the fractional remainder of each bit period is
            // carried, so the error cannot accumulate over the message.
            Assert.InRange(audio.Length, expected - 1, expected + 1);
        }

        [Fact]
        public void The_phase_is_continuous_across_every_change_of_tone()
        {
            // The property that keeps a transmitter from splattering. A step in
            // the waveform at each transition would be broadband energy well
            // outside the shift, over other people's contacts.
            //
            // One sample of a 2295 Hz tone at 48 kHz moves by at most
            // amplitude * 2*pi*f/rate, about 0.048 at amplitude 0.2. A tone
            // switch with independent oscillators can step by a full 2 *
            // amplitude, which is eight times that - so this threshold
            // separates the two cases with room to spare rather than measuring
            // anything finely.
            var audio = RttyModulator.ToAudio("RYRYRY", Rate, Mark, Space,
                                              RttyModulator.Baud45, amplitude: 0.2);

            var biggest = 0.0;
            for (int i = 1; i < audio.Length; i++)
                biggest = Math.Max(biggest, Math.Abs(audio[i] - audio[i - 1]));

            Assert.True(biggest < 0.10, $"biggest sample-to-sample step was {biggest:F4}");
        }

        [Fact]
        public void Idle_is_the_mark_tone_and_the_start_bit_is_the_space_tone()
        {
            const double baud = RttyModulator.Baud45;
            var samplesPerBit = Rate / baud;

            // 20 bits of idle, then the start element of the leading LTRS.
            var audio = RttyModulator.ToAudio("E", Rate, Mark, Space, baud, idleBitsBefore: 20);

            // Measure in the middle of the idle, well clear of either end.
            var idle = Measure(audio, (int)(samplesPerBit * 5), (int)(samplesPerBit * 10));
            Assert.True(idle.mark > idle.space * 10,
                $"idle should be mark: mark {idle.mark:F3} space {idle.space:F3}");

            // The start element begins one bit after the idle ends; sample the
            // middle half of it so filter edges cannot reach in.
            var startAt = (int)(samplesPerBit * 20);
            var start = Measure(audio, startAt + (int)(samplesPerBit * 0.25), (int)(samplesPerBit * 0.5));
            Assert.True(start.space > start.mark * 10,
                $"the start element should be space: mark {start.mark:F3} space {start.space:F3}");
        }

        [Theory]
        [InlineData(170)]
        [InlineData(425)]
        [InlineData(850)]
        public void The_two_tones_land_where_they_were_asked_to_for_any_shift(int shift)
        {
            // A listener meets all of these: 170 on the amateur bands, 425 and
            // 450 on the weather and press stations, 850 on military circuits.
            // The modulator is given two frequencies and has no table, so this
            // is really checking that nothing in it assumes a narrow shift.
            const double baud = RttyModulator.Baud50;
            var space = Mark + shift;
            var samplesPerBit = Rate / baud;

            var audio = RttyModulator.ToAudio("A", Rate, Mark, space, baud, idleBitsBefore: 20);

            var idle = Measure(audio, (int)(samplesPerBit * 5), (int)(samplesPerBit * 10),
                               Mark, space);
            Assert.True(idle.mark > idle.space * 10, $"shift {shift}: idle is not the mark tone");
        }

        /// <summary>
        /// How much mark and how much space is in a stretch of the audio, by
        /// correlating against each tone directly. A two-bin DFT - less code
        /// than arranging an FFT, and it needs no power-of-two window.
        /// </summary>
        private static (double mark, double space) Measure(
            float[] audio, int offset, int count, double markHz = Mark, double spaceHz = Space)
        {
            count = Math.Min(count, audio.Length - offset);
            return (Goertzel(audio, offset, count, markHz), Goertzel(audio, offset, count, spaceHz));
        }

        private static double Goertzel(float[] audio, int offset, int count, double hz)
        {
            double re = 0, im = 0;
            for (int i = 0; i < count; i++)
            {
                var a = 2 * Math.PI * hz * i / Rate;
                re += audio[offset + i] * Math.Cos(a);
                im += audio[offset + i] * Math.Sin(a);
            }
            return Math.Sqrt(re * re + im * im) / count;
        }
    }
}
