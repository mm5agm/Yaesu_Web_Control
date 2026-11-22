using RadioWebControl.Core.Services;

namespace RadioWebControl.Core.Tests;

// Linux by-id serial paths: GetPortNames never lists them, so a CAT or key
// port set to one used to be "not found", on a Linux host and in Docker.
public class SerialPortPathsTests
{
    private const string ById = "/dev/serial/by-id/usb-Silicon_Labs_CP2105_Dual_USB_to_UART_Bridge_Controller_01234567-if00-port0";

    [Fact]
    public void AListedPortIsPresentEverywhere()
        => Assert.True(SerialPortPaths.IsPresent("COM3", new[] { "COM3" }, linux: false, exists: _ => false));

    [Fact]
    public void AByIdPathThatExistsIsPresentOnLinux()
        => Assert.True(SerialPortPaths.IsPresent(ById, new[] { "/dev/ttyUSB0" }, linux: true, exists: p => p == ById));

    [Fact]
    public void InDockerTheListCanBeEmptyAndTheByIdNodeStillCounts()
        => Assert.True(SerialPortPaths.IsPresent(ById, Array.Empty<string>(), linux: true, exists: p => p == ById));

    [Theory]
    [InlineData(ById)]                       // unplugged
    [InlineData("/dev/ttyUSB7")]
    public void AMissingPathIsNotPresent(string port)
        => Assert.False(SerialPortPaths.IsPresent(port, new[] { "/dev/ttyUSB0" }, linux: true, exists: _ => false));

    [Fact]
    public void OffLinuxOnlyTheListCounts()
        => Assert.False(SerialPortPaths.IsPresent("COM9", new[] { "COM3" }, linux: false, exists: _ => true));

    [Fact]
    public void OnAHostByIdLinksToListedNodesAddNothing()
    {
        var real = new Dictionary<string, string> { [ById] = "/dev/ttyUSB0" };
        var names = SerialPortPaths.WithById(new[] { "/dev/ttyUSB0", "/dev/ttyUSB1" }, new[] { ById },
            p => real.TryGetValue(p, out var r) ? r : p);
        Assert.Equal(new[] { "/dev/ttyUSB0", "/dev/ttyUSB1" }, names);
    }

    [Fact]
    public void InDockerTheByIdNodesAreAdded()
    {
        var names = SerialPortPaths.WithById(Array.Empty<string>(), new[] { ById }, p => p);
        Assert.Equal(new[] { ById }, names);
    }
}
