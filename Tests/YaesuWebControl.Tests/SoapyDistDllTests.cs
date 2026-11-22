using System.Text;

namespace YaesuWebControl.Tests;

// YWC D#404: the installer shipped an old MinGW libusb-1.0.dll that lacks two
// functions hackrf.dll imports, so hackrf.dll failed to load ("The specified
// procedure could not be found") and the RTL-SDR scan crashed. The dev copy
// in C:\SoapySDR had the right libusb, so it never showed on the bench. This
// reads the PE import and export tables of every DLL the installer ships and
// checks that each function imported from another shipped DLL is exported by
// it. Pure file parsing, so it runs on the Linux CI runner too.
public sealed class SoapyDistDllTests
{
    private static readonly string[] Folders =
    {
        "soapysdr-dist/runtime",
        "soapysdr-dist/plugins",
        "soapysdr-dist/lib/SoapySDR/modules0.8-3",
    };

    [Fact]
    public void Every_import_between_shipped_SoapySDR_dlls_resolves()
    {
        var root = RepoRoot();
        var runtime = Directory.GetFiles(Path.Combine(root, "soapysdr-dist/runtime"), "*.dll")
            .ToDictionary(p => Path.GetFileName(p), p => p, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("libusb-1.0.dll", runtime.Keys);

        var exports = runtime.ToDictionary(kv => kv.Key, kv => Pe.Read(kv.Value).Exports, StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        int checkedImports = 0;
        foreach (var folder in Folders)
        {
            foreach (var dll in Directory.GetFiles(Path.Combine(root, folder), "*.dll"))
            {
                foreach (var (from, names) in Pe.Read(dll).Imports)
                {
                    if (!exports.TryGetValue(from, out var available)) continue; // a Windows DLL
                    foreach (var name in names)
                    {
                        checkedImports++;
                        if (!available.Contains(name))
                            missing.Add($"{folder}/{Path.GetFileName(dll)} needs {from}!{name}");
                    }
                }
            }
        }
        Assert.True(checkedImports > 50, $"Only {checkedImports} imports checked; is the PE parsing broken?");
        Assert.True(missing.Count == 0, "Missing exports:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Shipped_libusb_has_the_raw_io_functions_hackrf_needs()
    {
        var libusb = Pe.Read(Path.Combine(RepoRoot(), "soapysdr-dist/runtime/libusb-1.0.dll"));
        Assert.Contains("libusb_endpoint_set_raw_io", libusb.Exports);
        Assert.Contains("libusb_endpoint_supports_raw_io", libusb.Exports);
    }

    private sealed record Pe(HashSet<string> Exports, Dictionary<string, List<string>> Imports)
    {
        public static Pe Read(string path)
        {
            var d = File.ReadAllBytes(path);
            int pe = BitConverter.ToInt32(d, 0x3c);
            int sections = BitConverter.ToUInt16(d, pe + 6);
            int optSize = BitConverter.ToUInt16(d, pe + 20);
            int opt = pe + 24;
            bool pe64 = BitConverter.ToUInt16(d, opt) == 0x20b;
            int dataDirs = opt + (pe64 ? 112 : 96);
            var table = new List<(uint Va, uint Size, uint Raw)>();
            for (int i = 0; i < sections; i++)
            {
                int s = opt + optSize + i * 40;
                table.Add((BitConverter.ToUInt32(d, s + 12), Math.Max(BitConverter.ToUInt32(d, s + 8), BitConverter.ToUInt32(d, s + 16)), BitConverter.ToUInt32(d, s + 20)));
            }
            int Off(uint rva)
            {
                foreach (var (va, size, raw) in table)
                    if (rva >= va && rva < va + size) return (int)(rva - va + raw);
                throw new InvalidDataException($"RVA {rva:X} not in any section of {path}");
            }
            string Str(uint rva)
            {
                int o = Off(rva), e = Array.IndexOf(d, (byte)0, o);
                return Encoding.ASCII.GetString(d, o, e - o);
            }

            var exports = new HashSet<string>(StringComparer.Ordinal);
            uint exportRva = BitConverter.ToUInt32(d, dataDirs);
            if (exportRva != 0)
            {
                int e = Off(exportRva);
                int count = BitConverter.ToInt32(d, e + 24);
                int names = Off(BitConverter.ToUInt32(d, e + 32));
                for (int i = 0; i < count; i++) exports.Add(Str(BitConverter.ToUInt32(d, names + 4 * i)));
            }

            var imports = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            uint importRva = BitConverter.ToUInt32(d, dataDirs + 8);
            if (importRva != 0)
            {
                for (int o = Off(importRva); ; o += 20)
                {
                    uint lookup = BitConverter.ToUInt32(d, o), nameRva = BitConverter.ToUInt32(d, o + 12), iat = BitConverter.ToUInt32(d, o + 16);
                    if (nameRva == 0) break;
                    var list = new List<string>();
                    for (int t = Off(lookup != 0 ? lookup : iat); ; t += pe64 ? 8 : 4)
                    {
                        ulong v = pe64 ? BitConverter.ToUInt64(d, t) : BitConverter.ToUInt32(d, t);
                        if (v == 0) break;
                        bool byOrdinal = pe64 ? (v & (1UL << 63)) != 0 : (v & (1UL << 31)) != 0;
                        if (!byOrdinal) list.Add(Str((uint)(v & 0x7fffffff) + 2));
                    }
                    imports[Str(nameRva)] = list;
                }
            }
            return new Pe(exports, imports);
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Yaesu_Web_Control.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException($"No Yaesu_Web_Control.csproj above {AppContext.BaseDirectory}.");
    }
}
