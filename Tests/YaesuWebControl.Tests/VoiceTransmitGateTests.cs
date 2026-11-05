using Yaesu_Web_Control.Services.Voice;

namespace YaesuWebControl.Tests;

public class VoiceTransmitGateTests
{
    [Theory]
    [InlineData("TxOn")]
    [InlineData("AtuTune")]
    public void CommandsThatGoOnTheAirAreCovered(string intent)
    {
        Assert.True(VoiceTransmitGate.StartsTransmit(intent, null));
    }

    // Stopping has to stay easy, and the everyday commands keep the normal gate.
    [Theory]
    [InlineData("TxOff")]
    [InlineData("AtuOn")]
    [InlineData("AtuOff")]
    [InlineData("BandUp")]
    [InlineData("SetNoiseReduction")]
    public void OtherCommandsAreNotCovered(string intent)
    {
        Assert.False(VoiceTransmitGate.StartsTransmit(intent, null));
    }

    [Theory]
    [InlineData("TX1;")]
    [InlineData("TX2;")]
    [InlineData("tx1;")]              // lower case still reaches the radio
    [InlineData("TX1")]               // no terminator
    [InlineData("MX1;")]              // MOX on
    [InlineData("AC002;")]            // start a tuner cycle
    [InlineData("KY1;")]              // send from the keyer
    [InlineData("PB01;")]             // play a voice memory
    [InlineData("VX1;")]              // VOX on
    [InlineData("MD02;TX1;")]         // hidden behind a harmless command
    public void MacrosThatGoOnTheAirAreCovered(string cat)
    {
        Assert.True(VoiceTransmitGate.StartsTransmit("Macro", cat));
    }

    [Theory]
    [InlineData("TX0;")]              // back to receive
    [InlineData("TX;")]               // only reads the state
    [InlineData("MX0;")]              // MOX off
    [InlineData("AC001;")]            // tuner in line
    [InlineData("AC000;")]            // tuner bypassed
    [InlineData("AC;")]               // only reads the state
    [InlineData("PB;")]               // only reads the state
    [InlineData("PB00;")]             // stop a voice memory
    [InlineData("VX0;")]              // VOX off
    [InlineData("MD02;SH0;")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherMacrosAreNotCovered(string? cat)
    {
        Assert.False(VoiceTransmitGate.StartsTransmit("Macro", cat));
    }

    [Fact]
    public void TheBarIsWellAboveTheNormalOne()
    {
        Assert.True(VoiceTransmitGate.MinConfidence >= 0.8f);
    }
}
