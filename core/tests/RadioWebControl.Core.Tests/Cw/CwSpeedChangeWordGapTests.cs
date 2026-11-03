using RadioWebControl.Core.Services.Cw;
using Xunit;

namespace RadioWebControl.Core.Tests.Cw
{
    /// <summary>
    /// Word gaps after the speed changes under the reader.
    ///
    /// The gap window used to hold 48 gaps in milliseconds whatever speed they
    /// were measured at. `bench/iw2bta-cq-loop-14052-20261016f.wav` showed what
    /// that does: fast QSB fragments filled the window with short gaps, then a
    /// slow sender with 7-8 dit letter gaps arrived, the old gaps dragged the
    /// character gap down to its three-dit floor, and his letter gaps printed
    /// as word gaps - "I W 2 B TA". These key the same shape with known text.
    /// </summary>
    public class CwSpeedChangeWordGapTests
    {
        private static string Decode(float[] audio, double toneHz)
        {
            var engine = new CwDecoderEngine(new CwDecoderOptions { PitchHz = toneHz });

            var text = new System.Text.StringBuilder();
            const int frame = 480;
            for (int i = 0; i + frame <= audio.Length; i += frame)
                text.Append(engine.ProcessFrame(audio.AsSpan(i, frame)));
            text.Append(engine.Flush());
            return CwAccuracy.Normalise(text.ToString()).Trim();
        }

        [Fact]
        public void Slow_sender_after_a_fast_burst_keeps_his_callsign_whole()
        {
            var gen = new CwSignalGenerator { ToneHz = 600.0 };

            var audio = CwSignalGenerator.Concat(gen.LeadIn(),
                               gen.GenerateWithFist("TEST TEST DE G4ABC G4ABC TU EE", 36.0,
                                                    charGapDits: 3.0, wordGapDits: 7.0),
                               gen.Silence(0.6),
                               gen.GenerateWithFist("CQ CQ DE IW2BTA IW2BTA K", 14.0,
                                                    charGapDits: 7.5, wordGapDits: 16.0),
                               gen.Silence(0.5));

            string got = Decode(audio, 600.0);

            Assert.Contains("IW2BTA IW2BTA", got);
        }
    }
}
