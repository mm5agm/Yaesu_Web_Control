using System;
using RadioWebControl.Core.Services.Cw;
using Xunit;
using Xunit.Abstractions;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// The third readability check, and the one that catches an empty band.
    ///
    /// The mark-spread test asks whether short and long marks are cleanly
    /// separated, and the dit-only test asks whether the letters are all made
    /// of dits. A detector chattering on a dead band passes both. Its blips
    /// come in two lengths, so the spread is textbook; they alternate short
    /// and long, so the letters are E and T rather than E and I and S; and
    /// each one is flushed as its own character. bench/diag-dead.wav is twelve
    /// seconds of nothing at 21 MHz and read "SETE ET TE E T TE E", Readable
    /// 81%, with the speed railed at the 60 WPM ceiling and the lock lit.
    ///
    /// What gives it away is the shape of the characters, not of the marks.
    /// Morse cannot average near one element per character because the
    /// alphabet does not contain enough one-element letters: over the 11,497
    /// characters of the ARRL practice texts the sent mean is 2.67, and only
    /// 20.1% of characters are a single element. Decoded, across seven
    /// recordings from 12 to 2,489 characters and 5 to 40 WPM, the mean ran
    /// 2.33 to 3.14. The dead band ran 1.15.
    ///
    /// Sweeping the floor over those same recordings, 1.40, 1.70 and 2.00 are
    /// indistinguishable - every real file is untouched and the dead band is
    /// condemned at all three. 2.30 starts eating the ARRL files. So the floor
    /// sits at 1.70, midway across a gap with nothing in it.
    /// </summary>
    public class CwSingleElementReadabilityTests
    {
        private readonly ITestOutputHelper _out;

        public CwSingleElementReadabilityTests(ITestOutputHelper output) => _out = output;

        private static (string Text, CwDecoderEngine Engine) Run(float[] audio)
        {
            var engine = new CwDecoderEngine(new CwDecoderOptions { PitchHz = 600.0 });
            var text = new System.Text.StringBuilder();
            const int frame = 480;
            for (int i = 0; i < audio.Length; i += frame)
                text.Append(engine.ProcessFrame(audio.AsSpan(i, Math.Min(frame, audio.Length - i))));
            text.Append(engine.Flush());
            return (text.ToString(), engine);
        }

        /// <summary>
        /// The dead band, reproduced: alternating E and T with nothing longer.
        /// The timing here is perfect, which is the point - perfect timing is
        /// not what makes something Morse.
        /// </summary>
        [Fact]
        public void A_stream_of_one_element_letters_is_not_offered_as_readable()
        {
            var gen = new CwSignalGenerator();
            var audio = CwSignalGenerator.Concat(
                gen.LeadIn(),
                gen.Generate("ETE ET TE E T TE ET E TE T ETE TE ET TET E ETE T", 25.0),
                gen.Silence(0.5));

            var (text, engine) = Run(audio);
            _out.WriteLine($"{engine.Readability} \"{text.Trim()}\" {engine.WordsPerMinute:F1} wpm");

            Assert.Equal(CwReadability.Jumbled, engine.Readability);
        }

        /// <summary>
        /// And the lock goes with it. The lock has a hold so that a fade does
        /// not blink it off, but a fade is marks stopping or weakening; this is
        /// marks still arriving and not being Morse. Waiting the hold out is
        /// how the dead band kept reporting 58.6 wpm and "locked" for six
        /// seconds after the elements test had already condemned it.
        /// </summary>
        [Fact]
        public void The_speed_lock_does_not_survive_a_stream_of_one_element_letters()
        {
            var gen = new CwSignalGenerator();
            var audio = CwSignalGenerator.Concat(
                gen.LeadIn(),
                gen.Generate("ETE ET TE E T TE ET E TE T ETE TE ET TET E ETE T", 25.0),
                gen.Silence(0.5));

            var (_, engine) = Run(audio);
            Assert.False(engine.IsLocked, $"locked at {engine.WordsPerMinute:F1} wpm on an empty band");
        }

        /// <summary>
        /// The other side of it. Ordinary text contains plenty of E and T -
        /// they are the two commonest letters in English - so the check has to
        /// pass a real sentence that is one fifth single-element characters,
        /// which is what the ARRL texts measure.
        /// </summary>
        [Fact]
        public void Ordinary_text_full_of_es_and_ts_is_still_readable()
        {
            var gen = new CwSignalGenerator();
            var audio = CwSignalGenerator.Concat(
                gen.LeadIn(),
                gen.Generate("THE BETTER THE ANTENNA THE LESS THE POWER NEEDED " +
                             "TO BE HEARD AT THE OTHER END OF THE PATH", 25.0),
                gen.Silence(0.5));

            var (text, engine) = Run(audio);
            _out.WriteLine($"{engine.Readability} \"{text.Trim()}\"");

            Assert.Equal(CwReadability.Readable, engine.Readability);
            Assert.True(engine.IsLocked, "a clean sentence should hold the speed lock");
        }
    }
}
