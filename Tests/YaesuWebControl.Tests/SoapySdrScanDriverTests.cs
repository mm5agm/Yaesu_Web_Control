using Yaesu_Web_Control.Services.Sdr;

namespace YaesuWebControl.Tests;

// #404: when the whole SoapySDR scan crashes, each driver is scanned on its
// own. The driver names come from the worker's "Modules found:" log line, and
// a wrong name means that driver is never asked, so its devices go missing.
public sealed class SoapySdrScanDriverTests
{
    [Fact]
    public void Names_come_from_the_module_files()
    {
        var drivers = SoapySdrScan.DriversFromScanLog([
            @"SoapySDR.dll <- C:\Program Files\YWC\SoapySDR.dll",
            @"Modules found: C:\YWC\SoapySDR\modules0.8\airspySupport.dll, " +
            @"C:\YWC\SoapySDR\modules0.8\HackRFSupport.dll, C:/x/rtlsdrSupport.dll, " +
            @"C:\x\SoapySDRPlay3.dll, C:\x\librtlsdrSupport.dll",
        ]);
        Assert.Equal(["airspy", "hackrf", "rtlsdr", "sdrplay"], drivers);
    }

    [Fact]
    public void No_modules_line_or_none_found_gives_no_drivers()
    {
        Assert.Empty(SoapySdrScan.DriversFromScanLog(["SoapySDR.dll <- (not found next to the app; default search)"]));
        Assert.Empty(SoapySdrScan.DriversFromScanLog(["Modules found: (none in any search path)"]));
    }
}
