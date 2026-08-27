using System;
using System.IO;
using RadioWebControl.Core.Services.Cw;
using Xunit;
using Xunit.Abstractions;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// IsLocked is the panel's licence to print a speed, so it has to mean
    /// "this number is worth showing" and not merely "some marks arrived".
    ///
    /// The old rule was six trained marks and nothing else. On an empty band
    /// the noise blips that survive de-glitch train it just as well, so three
    /// minutes of recorded empty 15 m reported 51.4 wpm and "locked" - which
    /// an operator reads as a very fast station, not as a decoder with nothing
    /// to decode. There is no way to tell those apart from the panel, which is
    /// what makes the wrong number worse than no number.
    /// </summary>
    public class CwSpeedLockTests
    {
        private readonly ITestOutputHelper _out;

        public CwSpeedLockTests(ITestOutputHelper output) => _out = output;

        private static CwDecoderEngine Run(float[] audio, int rate, double pitch)
        {
            var engine = new CwDecoderEngine(new CwDecoderOptions
            {
                InputSampleRate = rate,
                PitchHz         = pitch,
            });

            int frame = rate / 100;
            for (int i = 0; i < audio.Length; i += frame)
                engine.ProcessFrame(audio.AsSpan(i, Math.Min(frame, audio.Length - i)));
            engine.Flush();
            return engine;
        }

        /// <summary>
        /// The recording, not a synthetic band. CwRecordedEmptyBandTests
        /// explains at length why generated noise cannot stand in for this.
        /// </summary>
        [Fact]
        public void An_empty_band_does_not_claim_a_speed()
        {
            var (audio, rate) = LoadFixture("empty-15m-8k.wav");
            var engine = Run(audio, rate, 700.0);

            _out.WriteLine($"empty band: {engine.WordsPerMinute:F1} wpm, " +
                           $"locked {engine.IsLocked}, {engine.Readability}");

            Assert.False(engine.IsLocked);
        }

        /// <summary>
        /// The other half, and the one that matters more: the lock must still
        /// come up on real Morse. A test that only proved the empty band is
        /// satisfied by IsLocked returning false forever.
        /// </summary>
        [Fact]
        public void Ordinary_sending_locks_the_speed()
        {
            var gen = new CwSignalGenerator();
            var audio = CwSignalGenerator.Concat(
                gen.LeadIn(),
                gen.Generate("CQ CQ DE MM5AGM MM5AGM K", 20.0));

            var engine = Run(audio, 48000, 600.0);

            _out.WriteLine($"signal: {engine.WordsPerMinute:F1} wpm, " +
                           $"locked {engine.IsLocked}, {engine.Readability}");

            Assert.True(engine.IsLocked);
            Assert.InRange(engine.WordsPerMinute, 5.0, 60.0);
        }

        /// <summary>
        /// The lock holds through a gap rather than blinking off in it.
        ///
        /// Readability dips on any deep fade, and a lock that dropped the
        /// instant it did would flicker the speed off and on across normal
        /// QSB. What separates a fade from a dead band is not the dip but how
        /// long it lasts, so the lock is given LockHoldMs to see the signal
        /// come back. Two seconds of silence is inside that; the empty-band
        /// recording above is minutes of it, and is not.
        /// </summary>
        [Fact]
        public void A_short_silence_does_not_drop_the_lock()
        {
            var gen = new CwSignalGenerator();
            var audio = CwSignalGenerator.Concat(
                gen.LeadIn(),
                gen.Generate("CQ CQ DE MM5AGM MM5AGM K", 20.0),
                gen.Silence(2.0));

            var engine = Run(audio, 48000, 600.0);

            _out.WriteLine($"after 2 s gap: {engine.WordsPerMinute:F1} wpm, " +
                           $"locked {engine.IsLocked}");

            Assert.True(engine.IsLocked);
        }

        private static (float[] audio, int rate) LoadFixture(string name)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Cw", "Fixtures", name);
            Assert.True(File.Exists(path), $"missing fixture {path}");

            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            reader.ReadBytes(12);                       // RIFF....WAVE
            int     rate     = 0;
            short   channels = 1;
            float[] audio    = Array.Empty<float>();

            while (stream.Position < stream.Length - 8)
            {
                string id   = new string(reader.ReadChars(4));
                int    size = reader.ReadInt32();
                long   next = stream.Position + size + (size & 1);

                if (id == "fmt ")
                {
                    reader.ReadInt16();                 // format tag
                    channels = reader.ReadInt16();
                    rate     = reader.ReadInt32();
                }
                else if (id == "data")
                {
                    int frames = size / 2 / Math.Max((int)channels, 1);
                    audio = new float[frames];
                    for (int i = 0; i < frames; i++)
                    {
                        audio[i] = reader.ReadInt16() / 32768f;
                        for (int c = 1; c < channels; c++) reader.ReadInt16();
                    }
                }

                stream.Position = next;
            }

            Assert.True(rate > 0 && audio.Length > 0, $"could not read {name}");
            return (audio, rate);
        }
    }
}
