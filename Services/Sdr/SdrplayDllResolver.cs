using System.Runtime.InteropServices;
using System.Text.Json;

namespace Yaesu_Web_Control.Services.Sdr
{
    /// <summary>
    /// Native-library resolver for <c>sdrplay_api.dll</c>. Standard Windows
    /// P/Invoke search (app directory → System32 → PATH) fails when the
    /// SDRplay API service is installed but its <c>x64\</c> directory wasn't
    /// added to PATH — observed in the wild on IK2XRW Alessandro's system
    /// (#53, 2026-06-26). This resolver tries the documented and standard
    /// install locations before letting the default loader fall back to
    /// PATH, so the DLL is found regardless of whether PATH is set.
    ///
    /// Shared between main YWC and Yaesu_Sdr_Worker (file-linked into the
    /// worker's csproj). Call <see cref="Register"/> once at process startup
    /// — before any P/Invoke into sdrplay_api fires.
    /// </summary>
    public static class SdrplayDllResolver
    {
        /// <summary>P/Invoke name SdrplayDevice uses — matches its DllName const.</summary>
        public const string DllName = "sdrplay_api";

        /// <summary>
        /// Hook the resolver into the calling assembly as the sole resolver.
        /// Use from the worker process (Yaesu_Sdr_Worker) where this is the
        /// only DLL needing path resolution. **Do not call from main YWC** —
        /// main YWC already has a combined resolver registered in Program.cs
        /// that calls <see cref="TryResolve"/> directly. SetDllImportResolver
        /// can only be called once per assembly; calling it twice throws.
        /// </summary>
        public static void Register()
        {
            NativeLibrary.SetDllImportResolver(
                System.Reflection.Assembly.GetExecutingAssembly(),
                (name, _, _) =>
                {
                    if (name == SoapySdrDllName)
                        return TryResolveSoapySdr(out _, out IntPtr sh) ? sh : IntPtr.Zero;
                    if (name != DllName) return IntPtr.Zero;
                    return TryResolve(out IntPtr h) ? h : IntPtr.Zero;
                });
        }

        /// <summary>P/Invoke name SoapySdrInterop uses — matches its DllName const.</summary>
        public const string SoapySdrDllName = "SoapySDR";

        /// <summary>
        /// Where the shipped SoapySDR.dll lives: <c>&lt;app&gt;\SoapySDR\bin</c>,
        /// with the build machine's <c>C:\SoapySDR\bin</c> as the developer
        /// fallback. The same two places main YWC's resolver in Program.cs
        /// looks; the worker needs them too, or a SoapySDR device only ever
        /// opens on a PC that happens to have SoapySDR.dll on the PATH.
        /// </summary>
        public static bool TryResolveSoapySdr(out string? path)
        {
            path = Path.Combine(AppContext.BaseDirectory, "SoapySDR", "bin", "SoapySDR.dll");
            if (File.Exists(path)) return true;
            path = @"C:\SoapySDR\bin\SoapySDR.dll";
            if (File.Exists(path)) return true;
            path = null;
            return false;
        }

        private static bool TryResolveSoapySdr(out string? path, out IntPtr handle)
        {
            handle = IntPtr.Zero;
            return TryResolveSoapySdr(out path) && NativeLibrary.TryLoad(path!, out handle);
        }

        /// <summary>
        /// Detect (without loading) the SDRplay install directory that the
        /// resolver would use, or null if nothing is found. Tries the user
        /// override first, then the standard Program Files locations.
        /// Returns the install root (the folder containing the x64
        /// subfolder), not the DLL path.
        /// </summary>
        public static string? DetectInstallDir()
        {
            // 1. User override
            var userPath = LoadConfiguredSdrplayInstallPath();
            if (!string.IsNullOrWhiteSpace(userPath) && DllExistsUnder(userPath))
                return userPath;

            // 2. Standard locations
            string[] standardDirs = new[]
            {
                @"C:\Program Files\SDRplay\API",
                @"C:\Program Files (x86)\SDRplay\API",
            };
            foreach (var dir in standardDirs)
            {
                if (DllExistsUnder(dir)) return dir;
            }

            return null;
        }

        // Checks for sdrplay_api.dll under either {installDir}\x64\ or
        // directly under installDir (some users may give the x64 path).
        private static bool DllExistsUnder(string installDir)
        {
            try
            {
                if (File.Exists(Path.Combine(installDir, "x64", "sdrplay_api.dll"))) return true;
                if (File.Exists(Path.Combine(installDir, "sdrplay_api.dll"))) return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Resolution order:
        ///   1. User-specified path in appsettings.user.json (SdrplayInstallPath)
        ///   2. Application directory (sdrplay_api.dll copied next to YWC.exe)
        ///   3. Standard install locations under Program Files
        ///   4. Return false → caller falls back to Windows default loader,
        ///      which honours PATH if SDRplay added itself.
        ///
        /// Each candidate is "{installDir}\x64\sdrplay_api.dll" — the SDRplay
        /// installer places the DLL in the x64 subfolder.
        /// </summary>
        public static bool TryResolve(out IntPtr handle)
        {
            handle = IntPtr.Zero;

            // 1. User override from appsettings.user.json
            var userPath = LoadConfiguredSdrplayInstallPath();
            if (!string.IsNullOrWhiteSpace(userPath) && TryLoadFromInstallDir(userPath, out handle))
                return true;

            // 2. Application directory — covers the copy-next-to-YWC workaround
            var appDir = AppContext.BaseDirectory;
            var sideBySide = Path.Combine(appDir, "sdrplay_api.dll");
            if (File.Exists(sideBySide) && NativeLibrary.TryLoad(sideBySide, out handle))
                return true;

            // 3. Standard install locations
            string[] standardDirs = new[]
            {
                @"C:\Program Files\SDRplay\API",
                @"C:\Program Files (x86)\SDRplay\API",
            };
            foreach (var dir in standardDirs)
            {
                if (TryLoadFromInstallDir(dir, out handle))
                    return true;
            }

            return false;
        }

        // Given an SDRplay install directory (the one that contains the
        // <c>x64</c> subfolder), try to load <c>x64\sdrplay_api.dll</c>.
        private static bool TryLoadFromInstallDir(string installDir, out IntPtr handle)
        {
            handle = IntPtr.Zero;
            try
            {
                var candidate = Path.Combine(installDir, "x64", "sdrplay_api.dll");
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
                    return true;
                // Some users may give the path with the x64 already included.
                candidate = Path.Combine(installDir, "sdrplay_api.dll");
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
                    return true;
            }
            catch { /* path may be malformed; just fall through */ }
            return false;
        }

        // Read the user's SdrplayInstallPath from appsettings.user.json
        // without spinning up the DI container — the resolver runs before
        // services are constructed. Mirrors LoadConfiguredHttpPort in
        // Program.cs in style.
        private static string? LoadConfiguredSdrplayInstallPath()
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MM5AGM", "Yaesu Web Control", "appsettings.user.json");
                if (!File.Exists(path)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("SdrplayInstallPath", out var p))
                    return p.GetString();
            }
            catch { }
            return null;
        }
    }
}
