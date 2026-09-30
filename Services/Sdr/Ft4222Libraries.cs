// Yaesu Web Control — finding FTDI's FT4222 libraries.
//
// The FT-710's internal scope is read through an FTDI FT4222H, which needs two
// closed-source FTDI libraries: D2XX (ftd2xx.dll) to open the device and
// LibFT4222 (LibFT4222-64.dll) for SPI. YWC is GPL-3 and cannot ship them, so
// the operator installs them and YWC says so when they are missing — the same
// "detect and instruct" approach as sdrplay_api.dll. Linked into the SDR
// worker as well as the main app: the main app only looks for the files (for
// the Settings note), the worker loads them.

using System.Runtime.InteropServices;

namespace Yaesu_Web_Control.Services.Sdr;

public static class Ft4222Libraries
{
    // Windows x64 names. The worker is Windows-only today; LibFT4222 also
    // exists for macOS and Linux, under other names, if that ever changes.
    public const string D2xxName      = "ftd2xx.dll";
    public const string LibFt4222Name = "LibFT4222-64.dll";

    public const string D2xxUrl      = "https://ftdichip.com/drivers/d2xx-drivers/";
    public const string LibFt4222Url = "https://ftdichip.com/software-examples/ft4222h-software-examples/";

    /// <summary>What to tell an operator when either library cannot be found.</summary>
    public const string MissingMessage =
        "The FT-710 scope needs FTDI's FT4222 libraries (ftd2xx.dll and LibFT4222-64.dll), " +
        "which Yaesu Web Control is not allowed to include because they are closed source. " +
        "Install FTDI's D2XX driver (" + D2xxUrl + "), then download LibFT4222 for Windows from " +
        LibFt4222Url +
        " and copy LibFT4222-64.dll into the Yaesu Web Control program folder.";

    /// <summary>
    /// Full path of a library, looking in the program folder, then System32,
    /// then PATH; or null. A file check only — nothing is loaded.
    /// </summary>
    public static string? Find(string fileName)
    {
        foreach (var dir in SearchDirectories())
        {
            try
            {
                var path = Path.Combine(dir, fileName);
                if (File.Exists(path)) return path;
            }
            catch (ArgumentException) { /* a malformed PATH entry */ }
        }
        return null;
    }

    public static bool BothPresent() => Find(D2xxName) is not null && Find(LibFt4222Name) is not null;

    /// <summary>
    /// Loads a library by the path Find gives, falling back to the normal
    /// search. Throws DllNotFoundException with the operator message.
    /// </summary>
    public static IntPtr Load(string fileName)
    {
        var path = Find(fileName);
        if (path is not null && NativeLibrary.TryLoad(path, out var h)) return h;
        if (NativeLibrary.TryLoad(fileName, out h)) return h;
        throw new DllNotFoundException($"{fileName} not found. {MissingMessage}");
    }

    private static IEnumerable<string> SearchDirectories()
    {
        yield return AppContext.BaseDirectory;
        yield return Environment.SystemDirectory;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return dir.Trim('"');
    }
}
