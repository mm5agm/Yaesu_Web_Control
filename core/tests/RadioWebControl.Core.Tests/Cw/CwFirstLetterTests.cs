using System;
using System.IO;
using System.Text;
using RadioWebControl.Core.Services.Cw;
using Xunit;
using Xunit.Abstractions;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// The first letter of a transmission on a band that has been hiss for a
    /// while. The presence gate opens only once it has heard enough marks, and
    /// on real band noise the level-only grace that would open it sooner never
    /// re-arms, so the marks that convince it are the first letter. Until
    /// CwToneDetector held them back for replay they were lost.
    ///
    /// The fixture is 23 s of 14.100 MHz recorded 2026-10-15, from 81 s into
    /// a capture of the NCDXF beacons, decimated to 8 kHz: noise, then OH2B
    /// in its scheduled slot at 22 wpm. Which beacon it is comes from the
    /// published schedule, not from anyone's copy. Before the replay it read
    /// TH2B - presence opened at the end of the O's second dah.
    ///
    /// It is a recording because synthetic noise does not reproduce this, for
    /// the same reason CwRecordedEmptyBandTests gives: white noise lets the
    /// grace re-arm, so a synthesised version of this test passed with and
    /// without the fix.
    /// </summary>
    public class CwFirstLetterTests
    {
        private readonly ITestOutputHelper _out;

        public CwFirstLetterTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void Keeps_the_first_letter_of_a_beacon_after_a_quiet_stretch()
        {
            var (audio, rate) = LoadFixture("ncdxf-oh2b-20m-8k.wav");

            var engine = new CwDecoderEngine(new CwDecoderOptions
            {
                InputSampleRate = rate,
                PitchHz         = 620.0,
                SearchWindowHz  = CwDecoderOptions.SearchWindowForFilterWidth(250),
            });
            var text = new StringBuilder();
            const int frame = 80;                       // 10 ms at the fixture's rate
            for (int i = 0; i < audio.Length; i += frame)
                text.Append(engine.ProcessFrame(audio.AsSpan(i, Math.Min(frame, audio.Length - i))));
            text.Append(engine.Flush());

            string decoded = text.ToString();
            _out.WriteLine($"\"{decoded}\"");

            Assert.Contains("OH2B", decoded);
        }

        /// <summary>Minimal PCM-16 mono reader, as in CwRecordedEmptyBandTests.</summary>
        private static (float[] audio, int rate) LoadFixture(string name)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Cw", "Fixtures", name);
            Assert.True(File.Exists(path), $"missing fixture {path}");

            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            reader.ReadBytes(12);                       // RIFF....WAVE
            int     rate  = 0;
            float[] audio = Array.Empty<float>();

            while (stream.Position < stream.Length - 8)
            {
                string id   = new string(reader.ReadChars(4));
                int    size = reader.ReadInt32();
                long   next = stream.Position + size + (size & 1);

                if (id == "fmt ")
                {
                    reader.ReadInt16();                 // format tag
                    reader.ReadInt16();                 // channels: mono
                    rate = reader.ReadInt32();
                }
                else if (id == "data")
                {
                    audio = new float[size / 2];
                    for (int i = 0; i < audio.Length; i++) audio[i] = reader.ReadInt16() / 32768f;
                }

                stream.Position = next;
            }

            Assert.True(rate > 0 && audio.Length > 0, $"could not read {name}");
            return (audio, rate);
        }
    }
}
