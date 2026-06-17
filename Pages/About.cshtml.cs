using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Win32;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    public class AboutModel : PageModel
    {
        private readonly ISettingsService _settingsService;
        private readonly RadioStateService _radioStateService;

        public AboutModel(ISettingsService settingsService, RadioStateService radioStateService)
        {
            _settingsService = settingsService;
            _radioStateService = radioStateService;
        }

        // Surfaced to the view for the Diagnostics block.
        public string Version           { get; private set; } = AppVersion.Current;
        public string ReleaseDate       { get; private set; } = AppVersion.ReleaseDate;
        public string RadioModel        { get; private set; } = "—";
        public string BandPlan          { get; private set; } = "—";
        public string SerialPort        { get; private set; } = "—";
        public int    BaudRate          { get; private set; }
        public bool   IsConnected       { get; private set; }
        public string SdrDevice         { get; private set; } = "(none configured)";
        public string DxClusterHost     { get; private set; } = "(off)";
        public string DxClusterCallsign { get; private set; } = "(blank)";
        public string DotNetVersion     { get; private set; } = System.Environment.Version.ToString();
        public string OsDescription     { get; private set; } = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        public string Cpu               { get; private set; } = ReadCpuInfo();
        public string Memory            { get; private set; } = ReadMemoryInfo();

        public async Task OnGetAsync()
        {
            var s = await _settingsService.GetSettingsAsync();
            RadioModel  = s.RadioModel;
            BandPlan    = s.BandPlan;
            SerialPort  = s.SerialPort;
            BaudRate    = s.BaudRate;
            IsConnected = _radioStateService.IsConnected;

            // Diagnostics shows whichever SDR(s) are configured. Two VFOs as
            // of v2.3.0; bug reports from users with one configured still show
            // the right device.
            var sdrKeys = new List<string>();
            if (!string.IsNullOrWhiteSpace(s.SdrDeviceKeyA)) sdrKeys.Add($"A: {s.SdrDeviceKeyA}");
            if (!string.IsNullOrWhiteSpace(s.SdrDeviceKeyB)) sdrKeys.Add($"B: {s.SdrDeviceKeyB}");
            if (sdrKeys.Count > 0)
                SdrDevice = string.Join("   ", sdrKeys);
            if (s.DxClusterEnabled && !string.IsNullOrWhiteSpace(s.DxClusterHost))
                DxClusterHost = $"{s.DxClusterHost}:{s.DxClusterPort}";
            if (!string.IsNullOrWhiteSpace(s.DxClusterLoginCallsign))
                DxClusterCallsign = s.DxClusterLoginCallsign;
        }

        // ── Host hardware probes (Windows-only — fine since YWC is WinExe) ──
        //
        // Added 2026-06-15 so bug reports show whether the user's shack PC
        // can realistically drive two SDRs + radio polling + spectrum render
        // without saturating CPU or running out of RAM. Cheap to compute,
        // no extra dependencies — registry + P/Invoke into kernel32.

        private static string ReadCpuInfo()
        {
            try
            {
                // Same key Task Manager uses for the CPU name shown in
                // Performance > CPU. Read-only, no admin needed.
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                var name = key?.GetValue("ProcessorNameString") as string;
                var cleaned = string.IsNullOrWhiteSpace(name) ? "(unknown)" : name.Trim();
                return $"{cleaned}, {System.Environment.ProcessorCount} logical cores";
            }
            catch
            {
                return $"(unknown), {System.Environment.ProcessorCount} logical cores";
            }
        }

        private static string ReadMemoryInfo()
        {
            try
            {
                var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref status))
                {
                    var totalGiB = status.ullTotalPhys / (1024.0 * 1024 * 1024);
                    return $"{totalGiB:0.#} GB";
                }
            }
            catch { }
            return "(unknown)";
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint  dwLength;
            public uint  dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }
}
