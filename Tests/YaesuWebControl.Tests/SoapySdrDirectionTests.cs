using Yaesu_Web_Control.Services.Sdr;

namespace YaesuWebControl.Tests;

// #404 (Edy YO5ED, RTL-SDR): YWC had RX = 0, which is SoapySDR's TX, so the
// RTL-SDR driver refused the stream ("RTL-SDR is RX only") and the log said
// only "setupStream returned null stream". These pin the constants to
// SoapySDR/Constants.h and that the driver's reason reaches the log.
public sealed class SoapySdrDirectionTests
{
    [Fact]
    public void Directions_match_SoapySDR()
    {
        Assert.Equal(0, SoapySdrInterop.TX);
        Assert.Equal(1, SoapySdrInterop.RX);
    }

    [Theory]
    [InlineData("RTL-SDR is RX only", " — RTL-SDR is RX only")]
    [InlineData("  trailing space \n", " — trailing space")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void The_driver_reason_is_added_to_the_error(string? lastError, string expected) =>
        Assert.Equal(expected, SoapySdrInterop.FormatReason(lastError));
}
