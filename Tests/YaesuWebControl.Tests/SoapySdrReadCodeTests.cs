using Yaesu_Web_Control.Services.Sdr;

namespace YaesuWebControl.Tests;

// #404 (Edy YO5ED, RTL-SDR, pre31): the first readStream calls timed out (-1)
// while the RTL-SDR started up, and YWC treated that as fatal, so the worker
// restarted every few seconds and the spectrum never appeared. Timeouts and
// overflows are retried; real errors still stop the stream.
public sealed class SoapySdrReadCodeTests
{
    [Theory]
    [InlineData(-1, true)]   // TIMEOUT
    [InlineData(-4, true)]   // OVERFLOW
    [InlineData(-2, false)]  // STREAM_ERROR
    [InlineData(-3, false)]  // CORRUPTION
    [InlineData(-5, false)]  // NOT_SUPPORTED
    [InlineData(-7, false)]  // UNDERFLOW
    public void Only_timeout_and_overflow_are_retried(int code, bool transient) =>
        Assert.Equal(transient, SoapySdrDevice.IsTransient(code));

    [Theory]
    [InlineData(-1, "TIMEOUT")]
    [InlineData(-4, "OVERFLOW")]
    [InlineData(-2, "STREAM_ERROR")]
    [InlineData(-99, "unknown")]
    public void Codes_are_named_as_in_SoapySDR(int code, string name) =>
        Assert.Equal(name, SoapySdrDevice.ErrorName(code));

    [Fact]
    public void A_silent_device_is_given_a_few_seconds() =>
        Assert.InRange(SoapySdrDevice.NoSamplesLimitMs, 2000, 10000);
}
