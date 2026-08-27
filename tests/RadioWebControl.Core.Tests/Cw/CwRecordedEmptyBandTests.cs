using System;
using System.IO;
using System.Collections.Generic;
using RadioWebControl.Core.Services.Cw;
using Xunit;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// Thirty seconds of empty 15 m, recorded 2026-08-27 at 21.005 MHz and
    /// decimated to the detector's own 8 kHz work rate.
    ///
    /// This test exists because the synthesised version of it does not work.
    /// CwNoiseIsNotASignalTests generates noise with a random-walk gain, and
    /// once its wander was calibrated against what a band actually does -
    /// sigma 0.05 to 0.12 at tau around 0.6 s - the level test alone rejected
    /// every vector, so the whole file passed with the keying gate bypassed
    /// entirely. It only failed at wander five to twenty times reality, which
    /// is not noise at all but slow amplitude keying. Three separate attempts
    /// at a synthetic empty-band test passed with and without the fix.
    ///
    /// The recording does not have that problem: bypass the gate and it reads
    /// 97% present, restore it and it reads 21%, nearly all of which is the
    /// deliberate opening grace. Whatever makes a real band look like a signal
    /// is not in the amplitude statistics the generator reproduces, so until
    /// someone works out what it is, the recording is the regression test.
    /// </summary>
    public class CwRecordedEmptyBandTests
    {
        private const double Pitch = 700.0;

        [Fact]
        public void A_recording_of_an_empty_band_is_not_read_as_a_signal()
        {
            var (audio, rate) = LoadFixture("empty-15m-8k.wav");

            var detector = new CwToneDetector(new CwToneDetectorOptions
            {
                InputSampleRate = rate,
                PitchHz         = Pitch,
                TrackPitch      = true,
            });

            var samples = new List<CwToneSample>();
            detector.Process(audio, samples);
            detector.Flush(samples);
            Assert.NotEmpty(samples);

            // As in the synthetic tests: presence is judged after the grace has
            // had time to expire, keying over the whole recording.
            int skip = (int)(5.0 * samples.Count / (audio.Length / (double)rate));
            int settled = 0, keyed = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i].SignalPresent && i >= skip) settled++;
                if (samples[i].KeyDown) keyed++;
            }
            double present = (double)settled / Math.Max(1, samples.Count - skip);
            double keying  = (double)keyed / samples.Count;

            Assert.True(present < 0.10,
                $"an empty band was still called a signal {present:P0} of the time " +
                "once the grace had expired");
            // The keying bar is looser than the synthetic tests', and the
            // reason is worth writing down. Every one of these transitions
            // falls inside the opening grace - keying is gated by presence,
            // and presence after the grace is the 3% measured above. So this
            // is the cost of the grace stated plainly, and the assertion that
            // matters is the next one: those transitions are stray, they do
            // not have the lengths or the spacing of morse, and nothing
            // downstream ever assembles them into a character.
            Assert.True(keying < 0.05,
                $"an empty band read as keying {keying:P0} of the time");

            // The user-visible property, and the one the whole investigation
            // started from: an empty band must not produce text. Before the
            // keying gate this recording's band produced continuous E/I/T
            // chatter at a claimed 13.7 dB and "locked".
            var engine = new CwDecoderEngine(new CwDecoderOptions
            {
                InputSampleRate = rate,
                PitchHz         = Pitch,
            });
            var text = new System.Text.StringBuilder();
            const int frame = 80;                       // 10 ms at the fixture's rate
            for (int i = 0; i < audio.Length; i += frame)
            {
                int n = Math.Min(frame, audio.Length - i);
                text.Append(engine.ProcessFrame(audio.AsSpan(i, n)));
            }
            text.Append(engine.Flush());

            string decoded = text.ToString().Trim();
            Assert.True(decoded.Length == 0,
                $"an empty band decoded as \"{decoded}\"");
        }

        /// <summary>Minimal PCM-16 reader; the fixtures are mono and headered plainly.</summary>
        private static (float[] audio, int rate) LoadFixture(string name)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Cw", "Fixtures", name);
            Assert.True(File.Exists(path), $"missing fixture {path}");

            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            reader.ReadBytes(12);                       // RIFF....WAVE
            int    rate     = 0;
            short  channels = 1;
            float[] audio   = Array.Empty<float>();

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
