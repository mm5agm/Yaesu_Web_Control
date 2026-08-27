using System;
using System.Text;
using RadioWebControl.Core.Services.Cw;
using Xunit;
using Xunit.Abstractions;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// A signal the operator can hear must not be invisible to the reader.
    ///
    /// On 2026-08-27 a 40 m station sat at 1640 Hz in the audio with a 3.2 kHz
    /// filter open. It was loud enough to hear plainly and it decoded German
    /// the moment the dial was moved to drop it onto the pitch - but until then
    /// the reader showed nothing, because the tone search was capped at 500 Hz
    /// either side of the pitch however far the radio's filter was opened.
    /// </summary>
    public class CwOffPitchSearchTests
    {
        private const string Message = "CQ CQ DE MM5AGM MM5AGM K";
        private readonly ITestOutputHelper _out;
        public CwOffPitchSearchTests(ITestOutputHelper output) => _out = output;

        private static string Decode(float[] audio, double pitchHz, double searchHz)
        {
            var engine = new CwDecoderEngine(new CwDecoderOptions
            {
                PitchHz        = pitchHz,
                SearchWindowHz = searchHz,
            });
            var text = new StringBuilder();
            const int frame = 480;
            for (int i = 0; i < audio.Length; i += frame)
                text.Append(engine.ProcessFrame(audio.AsSpan(i, Math.Min(frame, audio.Length - i))));
            text.Append(engine.Flush());
            return text.ToString();
        }

        [Theory]
        // Every row sits outside the old fixed +/-500 Hz window and inside the
        // filter the operator has open, so every row fails without the change.
        [InlineData(1640.0, 3200)]   // the one that was actually missed, on 40 m
        [InlineData(1500.0, 2400)]
        [InlineData(1250.0, 1200)]
        public void A_signal_away_from_the_pitch_is_found_when_the_filter_is_open(
            double toneHz, int filterWidthHz)
        {
            var gen   = new CwSignalGenerator { ToneHz = toneHz };
            var audio = CwSignalGenerator.Concat(gen.LeadIn(), gen.Generate(Message, 20.0));
            gen.AddNoise(audio, 15.0);

            double search  = CwDecoderOptions.SearchWindowForFilterWidth(filterWidthHz);
            string decoded = Decode(audio, 700.0, search);
            double score   = CwAccuracy.Score(decoded, Message);

            _out.WriteLine($"  {toneHz:F0} Hz in a {filterWidthHz} Hz filter " +
                           $"(search +/-{search:F0}): {score:P1}  \"{CwAccuracy.Normalise(decoded)}\"");
            Assert.True(score >= 0.90,
                $"a signal at {toneHz:F0} Hz inside a {filterWidthHz} Hz filter " +
                $"scored {score:P1}: {decoded}");
        }

        /// <summary>
        /// The risk the wider search introduces, and the reason the detector
        /// tracks narrowly once it is confident: with a whole SSB passband to
        /// hunt in, a louder station elsewhere in it must not capture a lock
        /// that is already established on a quieter one.
        /// </summary>
        [Fact]
        public void A_louder_neighbour_does_not_steal_an_established_lock()
        {
            var wanted = new CwSignalGenerator { ToneHz = 700.0, Amplitude = 0.25 };
            var audio  = CwSignalGenerator.Concat(wanted.LeadIn(), wanted.Generate(Message, 20.0));

            // A neighbour 800 Hz up the passband, four times the amplitude,
            // starting once the wanted signal is already locked.
            var loud  = new CwSignalGenerator { ToneHz = 1500.0, Amplitude = 1.0 };
            var noisy = CwSignalGenerator.Concat(loud.Silence(3.0),
                                                 loud.Generate("TEST TEST TEST DE OZ1ABC", 20.0));
            for (int i = 0; i < audio.Length && i < noisy.Length; i++)
                audio[i] += noisy[i];

            double search  = CwDecoderOptions.SearchWindowForFilterWidth(3200);
            string decoded = Decode(audio, 700.0, search);
            double score   = CwAccuracy.Score(decoded, Message);

            _out.WriteLine($"  wanted at 700 Hz under a 4x neighbour at 1500 Hz: " +
                           $"{score:P1}  \"{CwAccuracy.Normalise(decoded)}\"");
            Assert.True(score >= 0.80,
                $"the lock was pulled off the wanted signal, scoring {score:P1}: {decoded}");
        }
    }
}
