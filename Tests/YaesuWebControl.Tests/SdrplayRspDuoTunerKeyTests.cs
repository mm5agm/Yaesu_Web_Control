using Yaesu_Web_Control.Services.Sdr;

namespace YaesuWebControl.Tests;

/// <summary>
/// #165: while one RSPduo tuner streams, the SDRplay API hides the whole box,
/// so the Settings scan rebuilds the other tuner's entry from the key alone.
/// </summary>
public sealed class SdrplayRspDuoTunerKeyTests
{
    [Theory]
    [InlineData("sdrplay:hw3-2234077E34", "sdrplay:hw3-2234077E34-t2")]
    [InlineData("sdrplay:hw3-2234077E34-t2", "sdrplay:hw3-2234077E34")]
    public void RspDuo_GivesTheOtherTuner(string key, string other)
    {
        Assert.Equal(other, SdrplayDevice.OtherRspDuoTunerKey(key));
    }

    [Theory]
    [InlineData("sdrplay:hw6-2405242660")]   // RSP1B
    [InlineData("sdrplay:hw255-1234")]       // RSP1A
    [InlineData("sdrplay:0000000001")]       // legacy key, model unknown
    [InlineData("driver=rtlsdr,serial=0001")] // SoapySDR
    [InlineData("acc:main")]
    [InlineData("")]
    public void AnythingElse_HasNoOtherTuner(string key)
    {
        Assert.Null(SdrplayDevice.OtherRspDuoTunerKey(key));
    }

    [Fact]
    public void TheOtherTuner_IsLabelledAsSuch()
    {
        Assert.EndsWith("Tuner 2", SdrplayDevice.LabelForKey("sdrplay:hw3-2234077E34-t2"));
    }
}
