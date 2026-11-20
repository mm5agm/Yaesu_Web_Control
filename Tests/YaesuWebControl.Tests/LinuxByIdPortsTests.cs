using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// A /dev/serial/by-id path is never in SerialPort.GetPortNames(), so
    /// checking the CAT or key port against that list alone turned it away as
    /// "not found", on a Linux host and in Docker (2026-11-20). Every presence
    /// check goes through SerialPortPaths.IsPresent instead.
    /// </summary>
    public class LinuxByIdPortsTests
    {
        [Theory]
        [InlineData("Services/CatMultiplexerService.cs", 2)]
        [InlineData("Services/Cw/CwKeyingService.cs", 1)]
        public void PresenceChecksAllowByIdPaths(string relative, int expected)
        {
            string src = File.ReadAllText(Locate(relative));
            Assert.Equal(expected, Regex.Matches(src, @"SerialPortPaths\.IsPresent\(").Count);
            Assert.DoesNotMatch(@"GetPortNames\(\)\.Contains\(|ports\.Contains\(portName\)|availablePorts\.Contains\(portName\)", src);
        }

        [Fact]
        public void TheKeyLineListAddsByIdNodes()
            => Assert.Contains("SerialPortPaths.WithById(names)", File.ReadAllText(Locate("Services/Cw/SerialCwKeyLine.cs")));

        private static string Locate(string relative)
        {
            string native = relative.Replace('/', Path.DirectorySeparatorChar);
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, native);
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException(relative);
        }
    }
}
