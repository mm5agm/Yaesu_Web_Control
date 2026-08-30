using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Yaesu_Web_Control.Services;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
#if WINDOWS
using System.Windows.Forms;
#endif

// OpenCV's AVFoundation backend tries to spin the AppKit run loop to show the
// Camera TCC prompt. That only works on the main thread; from the Radio Display
// capture thread it logs "can not spin main run loop" and leaves the device
// half-open (IsOpened true, Read hangs, black MJPEG). Skip that path — the
// .app Info.plist + System Settings prompt already handle authorization.
if (OperatingSystem.IsMacOS())
    Environment.SetEnvironmentVariable("OPENCV_AVFOUNDATION_SKIP_AUTH", "1");

// ── Single-instance guard ────────────────────────────────────────────────────
const string MutexName = "Global\\Yaesu_Web_Control_SingleInstance";
var mutex = new Mutex(initiallyOwned: true, name: MutexName, out bool createdNew);

if (!createdNew)
{
#if WINDOWS
    // An OK-only "already running" box is a dead end when the running copy is a
    // stuck one: the operator sees no window, closes nothing, and every relaunch
    // hits the same box with Task Manager the only way out. Offer the two useful
    // actions instead: go to the running copy, or end it and start fresh.
#pragma warning disable CA1416
    var me = Process.GetCurrentProcess();
    Process[] others;
    try   { others = Process.GetProcessesByName(me.ProcessName).Where(p => p.Id != me.Id).ToArray(); }
    catch { others = Array.Empty<Process>(); }

    int existingPort = LoadConfiguredHttpPort();
    string url = $"http://localhost:{existingPort}";
    string who = others.Length > 0
        ? $"(process ID {string.Join(", ", others.Select(p => p.Id))})"
        : "(its window may be minimised to the system tray)";

    var choice = MessageBox.Show(
        $"Yaesu Web Control is already running {who}.\n\n" +
        $"Yes\t— open the running copy at {url}\n" +
        "No\t— close the running copy and start a new one\n" +
        "Cancel\t— do nothing",
        "Already Running",
        MessageBoxButtons.YesNoCancel,
        MessageBoxIcon.Information);

    if (choice == DialogResult.Yes)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        mutex.Dispose();
        return;
    }

    if (choice != DialogResult.No)
    {
        mutex.Dispose();
        return;
    }

    // Asked to end the running copy: request a clean close first, then force it.
    foreach (var p in others)
    {
        try
        {
            if (!p.CloseMainWindow() || !p.WaitForExit(4000))
                p.Kill(entireProcessTree: true);
            p.WaitForExit(4000);
        }
        catch { /* already gone, or access denied — the mutex retry below decides */ }
    }

    // The old process holds the mutex until it actually exits; give the handle a
    // moment to be released, then try to claim it ourselves.
    mutex.Dispose();
    Thread.Sleep(500);
    mutex = new Mutex(initiallyOwned: true, name: MutexName, out createdNew);
    if (!createdNew)
    {
        MessageBox.Show(
            "The running copy of Yaesu Web Control could not be closed.\n\n" +
            "End \"Yaesu_Web_Control.exe\" in Task Manager (Ctrl+Shift+Esc), then start it again.",
            "Still Running",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        mutex.Dispose();
        return;
    }
#pragma warning restore CA1416
#else
    Console.Error.WriteLine("Yaesu Web Control is already running.");
    mutex.Dispose();
    return;
#endif
}

// Keep the mutex alive for the lifetime of the process
AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { mutex.ReleaseMutex(); } catch { } mutex.Dispose(); };

// ── Helpers ──────────────────────────────────────────────────────────────────
static bool IsPortInUseException(Exception ex)
{
    var full = ex.ToString();
    return full.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
        || full.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase)
        || full.Contains("WSAEADDRINUSE", StringComparison.OrdinalIgnoreCase);
}

static string? GetPortOwner(int port)
{
    try
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName               = "netstat",
            Arguments              = "-ano",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            CreateNoWindow         = true
        });
        if (proc is null) return null;

        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        foreach (var line in output.Split('\n'))
        {
            if (line.Contains($":{port}") && line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1 && int.TryParse(parts[^1], out int pid))
                {
                    try   { return $"{Process.GetProcessById(pid).ProcessName} (PID {pid})"; }
                    catch { return $"PID {pid}"; }
                }
            }
        }
    }
    catch { }
    return null;
}

// Probe a TCP port to see if YWC can bind to it. Uses Socket.Bind on
// IPAddress.Any so we catch the full set of "port unavailable" cases:
//   - port already in use by another listener
//   - port in a Windows excluded range (WSL / Hyper-V / Docker)
//   - "socket access permissions" denial (some antivirus winsock hooks)
// All of these surface as a SocketException at bind time. We open and
// immediately close — there's a small race between this probe and Kestrel's
// real bind a few milliseconds later, but in practice that race window is
// short enough not to matter.
static bool IsPortFree(int port)
{
    // Enumerate every TCP port currently in LISTENING state on the system.
    // Way more reliable than trying to Bind() a probe socket: on Windows,
    // a second `Bind` to a port that another process is already listening
    // on can silently succeed (both end up in LISTENING; only one actually
    // receives traffic — the other "shadows" the first). SO_EXCLUSIVEADDRUSE
    // is supposed to prevent this but its semantics depend on flags both
    // sockets were created with, so we don't trust it. The active-listeners
    // enumeration sees the OS truth directly.
    var listeners = System.Net.NetworkInformation.IPGlobalProperties
        .GetIPGlobalProperties()
        .GetActiveTcpListeners();
    foreach (var endpoint in listeners)
    {
        if (endpoint.Port == port)
            return false;
    }
    return true;
}

// Pre-startup helper: read the user's configured HTTP port from
// appsettings.user.json (if it exists) without spinning up the full DI
// container. Falls back to 8080. Bounded to a sane range. We only need
// the one field so a minimal JSON parse keeps startup fast and avoids
// circular dependencies between port resolution and DI.
static int LoadConfiguredHttpPort()
{
    try
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Yaesu Web Control", "appsettings.user.json");
        if (!File.Exists(path)) return 8080;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.TryGetProperty("HttpPort", out var p) && p.TryGetInt32(out int port))
        {
            if (port >= 1 && port <= 65535) return port;
        }
    }
    catch { }
    return 8080;
}

static (bool enabled, int port) LoadConfiguredHttps()
{
    try
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Yaesu Web Control", "appsettings.user.json");
        if (!File.Exists(path)) return (false, 8443);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        bool enabled = doc.RootElement.TryGetProperty("HttpsEnabled", out var e) && e.ValueKind == JsonValueKind.True;
        int port = 8443;
        if (doc.RootElement.TryGetProperty("HttpsPort", out var p) && p.TryGetInt32(out int hp) && hp >= 1 && hp <= 65535)
            port = hp;
        return (enabled, port);
    }
    catch { }
    return (false, 8443);
}

#if WINDOWS
// ── Suppress Windows critical-error dialogs during DLL load ─────────────────
// When SoapySDR enumerates plugins (HackRF, RTL-SDR, Airspy, etc.), Windows
// tries to resolve each plugin's import table. If a plugin's dependencies
// conflict with whatever happens to be on the user's system — e.g.
// system32 has a newer hackrf.dll that needs libusb 1.0.27+ functions while
// YWC bundles an older libusb-1.0.dll, OR vice versa — Windows pops up a
// modal "Entry Point Not Found" dialog and waits for the user to click OK.
// That's startling and unhelpful since YWC handles plugin load failures
// gracefully anyway (the affected SDR just doesn't appear in the device list).
//
// SEM_FAILCRITICALERRORS + SEM_NOOPENFILEERRORBOX suppress the dialog so the
// process can fail the DLL load silently and carry on. Reported by the user
// on v2.3.1 — the Settings-page auto-scan triggered the dialog because a
// system32 hackrf.dll from some other SDR software conflicted with YWC's
// bundled libusb.
NativeWin32.SetErrorMode(NativeWin32.SEM_FAILCRITICALERRORS | NativeWin32.SEM_NOOPENFILEERRORBOX);

// ── Native library resolver (SoapySDR + sdrplay_api) ────────────────────────
// .NET P/Invoke on Windows does not search PATH directories by default, so
// DLLs that aren't next to the app or in System32 silently fail to load.
// One resolver lambda handles both DLLs — SetDllImportResolver can only be
// called *once* per assembly, so anything we want to resolve has to share
// this single registration.
//
// SDRplay history: observed on IK2XRW Alessandro's system (#53, 2026-06-26)
// where the SDRplay install didn't add its bin folder to PATH, so the SDR
// scan returned nothing. SdrplayDllResolver.TryResolve tries the user-
// configured path, the app directory, then the standard Program Files
// locations before falling back to default search.
NativeLibrary.SetDllImportResolver(
    System.Reflection.Assembly.GetExecutingAssembly(),
    static (name, _, _) =>
    {
        if (name == "SoapySDR")
        {
            // Installed layout: <app>\SoapySDR\bin\SoapySDR.dll
            var path = Path.Combine(AppContext.BaseDirectory, "SoapySDR", "bin", "SoapySDR.dll");
            // Developer fallback: C:\SoapySDR\bin\SoapySDR.dll (build machine only)
            if (!File.Exists(path))
                path = @"C:\SoapySDR\bin\SoapySDR.dll";
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out IntPtr h))
                return h;
        }
        else if (name == Yaesu_Web_Control.Services.Sdr.SdrplayDllResolver.DllName)
        {
            if (Yaesu_Web_Control.Services.Sdr.SdrplayDllResolver.TryResolve(out IntPtr h))
                return h;
        }
        return IntPtr.Zero;   // fall back to default resolution for all other DLLs
    });
#endif

// ── Serilog file logging ────────────────────────────────────────────────────
// YWC is a WinExe (no console window) so stdout-based loggers are invisible.
// Wire up Serilog with a rolling-daily file sink under %APPDATA% so we have a
// readable record of what the app did — essential for diagnosing shutdown
// hangs, CAT timeouts, SDR init failures and anything else the user can't see.
var logDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "MM5AGM", "Yaesu Web Control", "logs");
try { Directory.CreateDirectory(logDir); } catch { /* fall through, Serilog will surface the problem */ }

// Apply the saved "Detailed logging (for bug reports)" flag before the first
// line is written, so a user who ticked it gets the startup sequence captured.
// Read straight off disk — the DI container does not exist yet, and startup is
// over before it would.
Yaesu_Web_Control.Services.LogLevelController.Apply(
    Yaesu_Web_Control.Services.LogLevelController.ReadDetailedLoggingFromDisk());

const string LogOutputTemplate =
    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

Log.Logger = new LoggerConfiguration()
    // Controlled by a switch rather than fixed, so the Settings checkbox takes
    // effect immediately instead of at the next restart.
    .MinimumLevel.ControlledBy(Yaesu_Web_Control.Services.LogLevelController.Switch)
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore.SignalR", Serilog.Events.LogEventLevel.Warning)
    // Keep Hosting.Lifetime at Information so we see exactly when
    // StopApplication is called and when each hosted service's StopAsync runs
    // — invaluable for diagnosing shutdown stalls.
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
    .Enrich.FromLogContext()
    // Wrap the file sink in Serilog.Sinks.Async so log writes happen on a
    // dedicated background thread rather than on the calling thread. The bare
    // synchronous File sink — especially with shared:true (a cross-process
    // mutex taken per event) — blocks whichever thread logs, and under the
    // startup logging flood (54-command init burst + concurrent meter polling,
    // each CAT response emitting several lines) that blocked dozens of
    // thread-pool threads at once. The pool then injected replacements at only
    // ~1/sec, dilating the whole app — including the init burst's Task.Delay
    // continuations — to roughly 1 Hz, so the init sequence never reached the
    // DT0 step and the app hung at "Initializing". Intermittent because it
    // depends on disk / AV / file-lock timing (issue #73, wa6auf). Dropped
    // shared:true as well — YWC is the only writer of this file.
    //
    // Two separate files, deliberately.
    //
    // ywc-.log is the history, and it must survive: it is capped at
    // Information regardless of the switch, so turning Detailed logging on
    // cannot flood it or push older days out of the 7-day retention. That
    // matters because the whole argument for logging by default is catching
    // the intermittent fault nobody could reproduce on request — a history
    // that a stray checkbox can erase is not a history.
    //
    // ywc-detail-.log only exists while Detailed logging is on, and carries
    // everything including Debug. Measured at roughly 1,300 lines/second on a
    // live radio (~160 KB/s), which is why it gets its own file, its own size
    // cap and a short retention rather than sharing the normal budget.
    .WriteTo.Async(a => a.File(
        Path.Combine(logDir, "ywc-.log"),
        restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        // Never trips in normal use (~1 MB/day); a backstop, not a policy.
        fileSizeLimitBytes: 25L * 1024 * 1024,
        rollOnFileSizeLimit: true,
        flushToDiskInterval: TimeSpan.FromSeconds(1),
        outputTemplate: LogOutputTemplate))
    .WriteTo.Conditional(
        _ => Yaesu_Web_Control.Services.LogLevelController.IsDetailed,
        w => w.Async(a => a.File(
            Path.Combine(logDir, "ywc-detail-.log"),
            rollingInterval: RollingInterval.Day,
            // Three files at 50 MB bounds this at 150 MB even if a user ticks
            // the box and forgets. A detailed capture is meant to be minutes
            // long, so losing the oldest of three is no loss at all.
            retainedFileCountLimit: 3,
            fileSizeLimitBytes: 50L * 1024 * 1024,
            rollOnFileSizeLimit: true,
            flushToDiskInterval: TimeSpan.FromSeconds(1),
            outputTemplate: LogOutputTemplate)))
    .CreateLogger();

Log.Information("Yaesu Web Control starting (v{Version})", Yaesu_Web_Control.AppVersion.Current);

// Raise the thread-pool floor so cold start doesn't bottleneck on the pool's
// ~1/sec starvation-recovery thread injection. Startup fires many concurrent
// hosted services (radio init burst, meter polling, rigctld, SignalR) at once;
// if any of them briefly blocks a pool thread, a low floor forces new work to
// wait ~1s per thread for the pool to grow. A modest floor absorbs that spike.
// Belt-and-suspenders alongside the async logging sink above (issue #73).
{
    ThreadPool.GetMinThreads(out int minWorker, out int minIo);
    int targetWorker = Math.Max(minWorker, Math.Max(16, Environment.ProcessorCount * 2));
    int targetIo = Math.Max(minIo, 16);
    ThreadPool.SetMinThreads(targetWorker, targetIo);
    Log.Information("ThreadPool min threads set: worker {Worker} (was {OldWorker}), IO {Io} (was {OldIo}); processors={Cpu}",
        targetWorker, minWorker, targetIo, minIo, Environment.ProcessorCount);
}

// ContentRoot defaults to Directory.GetCurrentDirectory(). That is wrong when
// macOS launches the .app (cwd is "/" or ~) and can be wrong for a Windows
// shortcut whose "Start in" folder is missing — wwwroot/pictures/USER_MANUAL.md
// then resolve nowhere and the UI loads without CSS/JS. Prefer the process cwd
// when it already contains wwwroot (`dotnet run` from the repo, published exe
// started from its folder); otherwise fall back to the apphost directory.
var contentRootBaseDir = AppContext.BaseDirectory;
var contentRootCwd = Directory.GetCurrentDirectory();
var contentRoot = Directory.Exists(Path.Combine(contentRootCwd, "wwwroot"))
    ? contentRootCwd
    : Directory.Exists(Path.Combine(contentRootBaseDir, "wwwroot"))
        ? contentRootBaseDir
        : contentRootCwd;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRoot,
});

// Cap the host's overall shutdown timeout. Default is 30 s (which we hit on
// every tray Exit before adding this cap); 2 s is plenty for our user
// services to wind down their StopAsync routines. Tracked in the project
// todo memory.
builder.Services.Configure<HostOptions>(opts =>
{
    opts.ShutdownTimeout = TimeSpan.FromSeconds(2);
});

// Must be registered BEFORE CalibrationStorage — the storage constructor
// injects it so the median-based contributions store sits underneath the
// dev-only import path. See docs/design/calibration-contributions-port-from-iwc.md.
builder.Services.AddSingleton<CalibrationContributionsStore>();
builder.Services.AddSingleton<CalibrationStorage>();
builder.Services.AddSingleton<ICalibrationService, CalibrationService>();

// ADD SIGNALR EARLY (before services that depend on IHubContext):
builder.Services.AddSignalR();

// Register the persistence service (no hub dependency)
builder.Services.AddSingleton<RadioStatePersistenceService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RadioStatePersistenceService>());

// Band edges for the operator's own IARU region, read from
// wwwroot/bandplan.default.json — the same file the browser overlays at
// startup. RadioStateService resolves BandA/BandB through this, so the server
// and the waterfall can no longer disagree about where a band ends.
builder.Services.AddSingleton<IBandPlanService, BandPlanService>();

// Register RadioStateService and CatMessageBuffer as singletons
builder.Services.AddSingleton<RadioStateService>();
builder.Services.AddSingleton<CatMessageBuffer>();

// Register CatMessageDispatcher as singleton
builder.Services.AddSingleton<CatMessageDispatcher>();

// Register CatMultiplexerService as singleton
builder.Services.AddSingleton<CatMultiplexerService>();

// Register the main CAT client for the web app
builder.Services.AddSingleton<ICatClient, MultiplexedCatClient>();

// Tells the browser when an external program asked for a frequency the radio
// cannot tune. Shared by RigctldServer and WsjtxUdpService so the throttle
// state is common to both.
builder.Services.AddSingleton<FrequencyRejectionNotifier>();

// Register the rigctld server as a background service
builder.Services.AddHostedService<RigctldServer>();

// Register your settings service
builder.Services.AddSingleton<ISettingsService, SettingsService>();

// Remote radio audio (browser ↔ USB) — opt-in; devices open only while a client is connected.
builder.Services.AddSingleton<Yaesu_Web_Control.Services.Audio.AudioSessionManager>();
builder.Services.AddSingleton<Yaesu_Web_Control.Services.Audio.RadioAudioBridgeService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Yaesu_Web_Control.Services.Audio.RadioAudioBridgeService>());

// The CW reader listens to the audio bridge rather than opening the capture
// device itself, so it must be a singleton alongside it: one decoder, one
// subscription, one piece of decoded text however many browser tabs are open.
builder.Services.AddSingleton<Yaesu_Web_Control.Services.Cw.BridgeCwAudioSource>();
builder.Services.AddSingleton<Yaesu_Web_Control.Services.Cw.CwReaderService>();
// Radio Display (USB UVC / HDMI capture → MJPEG) — opt-in; capture opens while viewers connect.
builder.Services.AddSingleton<Yaesu_Web_Control.Services.Video.VideoSessionManager>();
builder.Services.AddSingleton<Yaesu_Web_Control.Services.Video.VideoCaptureService>();

// Audio filter EX address map — loaded once at startup from
// wwwroot/data/audio-filter-ex-map.json; used by the Audio Filter popout
// controller endpoints to translate per-radio menu addresses.
builder.Services.AddSingleton<AudioFilterMapService>();


// Add after existing service registrations
builder.Services.AddHostedService<MeterPollingService>();

#if WINDOWS
// SDR spectrum display — reads IQ samples, computes FFT, broadcasts via SignalR
// Registered as singleton so the span-change API endpoint can call RequestRestart().
builder.Services.AddSingleton<Yaesu_Web_Control.Services.Sdr.SdrManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Yaesu_Web_Control.Services.Sdr.SdrManager>());
#endif

// Register the radio state service — reuse the same singleton instance as RadioStateService
builder.Services.AddSingleton<IRadioStateService>(sp => sp.GetRequiredService<RadioStateService>());

// Register the radio initialization service
builder.Services.AddSingleton<RadioInitializationService>();

// VC Tune preselector control
builder.Services.AddSingleton<CatRequestSemaphore>();
builder.Services.AddSingleton<IVCTuneCommandBuilder, VCTuneCommandBuilder>();
builder.Services.AddSingleton<IVCTuneResponseParser, VCTuneResponseParser>();
builder.Services.AddSingleton<IVCTuneStateMachine, VCTuneStateMachine>();
builder.Services.AddSingleton<IVCTuneConfigurationStore, VCTuneConfigurationStore>();
builder.Services.AddSingleton<VCTuneDiagnostics>();
builder.Services.AddSingleton<VCTuneHelpProvider>();
builder.Services.AddSingleton<VCTuneModule>();
builder.Services.AddSingleton<VCTuneIntegrationHarness>();
builder.Services.AddSingleton<IVcTuneService, VcTuneService>();
builder.Services.AddSingleton<VCTuneViewModel>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<RadioInitializationService>());

// ADD THIS LINE for Razor Pages support:
builder.Services.AddRazorPages();

// ── HTTP port resolution ────────────────────────────────────────────────────
// Pick the port BEFORE Kestrel binds, so we can fall back gracefully if the
// user's configured port (default 8080) is held by another program. We try
// the configured port plus the nine above it; whichever is free first wins.
// The chosen port is published as a singleton HttpPortInfo so the browser
// launcher, system tray, and Settings UI all read the same value (Issue #13).
int basePort = LoadConfiguredHttpPort();
int chosenPort = -1;
var triedPorts = new List<int>();
for (int candidate = basePort; candidate < basePort + 10 && candidate <= 65535; candidate++)
{
    triedPorts.Add(candidate);
    if (IsPortFree(candidate))
    {
        chosenPort = candidate;
        break;
    }
}
if (chosenPort < 0)
{
    var diag = string.Join("\n",
        triedPorts.Select(p => $"  {p,5} — {GetPortOwner(p) ?? "unknown / reserved"}"));
    var portMsg =
        $"Yaesu Web Control couldn't find a free TCP port to listen on.\n\n" +
        $"Tried ports {triedPorts.First()}–{triedPorts.Last()}:\n\n{diag}\n\n" +
        $"Either close one of those programs, or change HttpPort in the appsettings.user.json\n" +
        $"under the MM5AGM/Yaesu Web Control application-data folder to a free port (e.g. 9080), then restart.";
#if WINDOWS
#pragma warning disable CA1416
    MessageBox.Show(portMsg, "No free port available", MessageBoxButtons.OK, MessageBoxIcon.Error);
#pragma warning restore CA1416
#else
    Console.Error.WriteLine(portMsg);
#endif
    return;
}

// Force the web host to use the chosen port on all interfaces.
// Optional HTTPS (self-signed) dual-binds when enabled and a cert exists.
var (httpsWanted, httpsPortCfg) = LoadConfiguredHttps();
bool httpsActive = false;
int? httpsListenPort = null;
if (httpsWanted)
{
    if (Yaesu_Web_Control.Services.Audio.HttpsCertificateService.CertificateExists)
    {
        if (IsPortFree(httpsPortCfg) || httpsPortCfg == chosenPort)
        {
            httpsActive = true;
            httpsListenPort = httpsPortCfg;
        }
        else
        {
            Log.Warning("HTTPS enabled but port {Port} is in use ({Owner}); staying HTTP-only",
                httpsPortCfg, GetPortOwner(httpsPortCfg) ?? "unknown");
        }
    }
    else
    {
        Log.Warning("HTTPS enabled but no certificate at {Path}; generate one in Settings and restart",
            Yaesu_Web_Control.Services.Audio.HttpsCertificateService.CertificatePath);
    }
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(chosenPort);
    if (httpsActive && httpsListenPort is int hp)
    {
        options.ListenAnyIP(hp, listen =>
        {
            listen.UseHttps(Yaesu_Web_Control.Services.Audio.HttpsCertificateService.CertificatePath,
                Yaesu_Web_Control.Services.Audio.HttpsCertificateService.PfxPassword);
        });
        Log.Information("HTTPS listening on 0.0.0.0:{Port}", hp);
    }
});

// Publish the chosen port so every consumer reads from one source of truth.
builder.Services.AddSingleton(new HttpPortInfo(chosenPort, httpsListenPort, httpsActive));

builder.Services.AddSingleton<BrowserLauncher>();
#if WINDOWS
// System tray icon — gives operators a visible "YWC is running" indicator
// and a clean Exit menu. Implemented as an STA-threaded hosted service.
builder.Services.AddHostedService<SystemTrayService>();
#else
// macOS menu-bar status item (Avalonia TrayIcon). Driven from Program.cs on
// the main thread after Kestrel StartAsync — AppKit forbids a background UI thread.
builder.Services.AddSingleton<MacSystemTrayService>();
#endif

// Register WSJT-X UDP listener as a singleton so it can be injected into controllers
builder.Services.AddSingleton<WsjtxUdpService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WsjtxUdpService>());

// Register process status cache service for efficient process lookups
builder.Services.AddSingleton<ProcessStatusCacheService>();

// Register radio memories service
builder.Services.AddSingleton<Yaesu_Web_Control.Services.MemoryService>();
builder.Services.AddSingleton<Yaesu_Web_Control.Services.MemoryBankService>();

// Register DX cluster service — single instance shared between controllers and
// the background hosted service so the API can read the spot buffer.
builder.Services.AddSingleton<Yaesu_Web_Control.Services.DxClusterService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Yaesu_Web_Control.Services.DxClusterService>());

// VCTuneRecognizer is needed on all platforms (VCTuneModule notifies it of
// P6 availability). The rest of the SAPI voice stack is Windows-only.
builder.Services.AddSingleton<Yaesu_Web_Control.Services.Voice.VCTuneRecognizer>();
#if WINDOWS
// Voice control (in-process SAPI). VoiceControlService is the IHostedService
// that owns the SpeechRecognitionEngine; IntentDispatcher maps recognised
// intents to CAT actions; VoiceTtsService speaks confirmation phrases;
// VoiceController exposes /api/voice/*. See docs/VoiceControl/v1-plan.md.
builder.Services.AddSingleton<Yaesu_Web_Control.Services.Voice.IntentDispatcher>();
builder.Services.AddSingleton<Yaesu_Web_Control.Services.Voice.VoiceTtsService>();
builder.Services.AddSingleton<Yaesu_Web_Control.Services.Voice.VoicePhraseStore>();
builder.Services.AddSingleton<Yaesu_Web_Control.Services.Voice.VoiceControlService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Yaesu_Web_Control.Services.Voice.VoiceControlService>());
#endif

// Route everything through Serilog (file sink configured above). The previous
// console + filter chain is gone — it was invisible in a WinExe anyway, and
// the file sink captures Information+ globally so we can read what happened
// after the fact without a console window.
builder.Logging.ClearProviders();
builder.Host.UseSerilog();


try
{
    var app = builder.Build();

    // Middleware to force Content-Language: en on all responses
    app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() => {
            if (!context.Response.Headers.ContainsKey("Content-Language"))
            {
                context.Response.Headers.Append("Content-Language", "en");
            }
            return System.Threading.Tasks.Task.CompletedTask;
        });
        await next();
    });

    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
    }
    else
    {
        app.UseExceptionHandler("/Error");
        app.UseHsts();
    }

    // Static files must be REVALIDATED, not trusted from cache.
    //
    // Without an explicit Cache-Control, ASP.NET Core sends only Last-Modified
    // and ETag, which leaves the browser free to apply heuristic freshness —
    // commonly a tenth of the file's age. A file that was already a fortnight
    // old when the browser cached it therefore stays "fresh" for a day or more,
    // and the browser will not so much as ask whether it changed.
    //
    // Installing a new version over the top replaces the file on disk but does
    // nothing to that cached copy, so an upgrading user gets the new page with
    // the old JavaScript behind it and any fix living in that JavaScript simply
    // never arrives. Ctrl+F5 clears it, which is not something an operator
    // should have to know. This was found in IWC, where it had silently
    // swallowed both headline fixes of a release; YWC carried the same hole.
    //
    // "no-cache" does NOT mean "do not store": the browser keeps the file and
    // revalidates it, so an unchanged file costs one conditional GET answered
    // with a 304 and no body. On a localhost or LAN app that is free, and it is
    // the price of never shipping a fix that fails to arrive.
    app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = ctx =>
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate";
        }
    });
    var picturesPath = System.IO.Path.Combine(app.Environment.ContentRootPath, "pictures");
    if (System.IO.Directory.Exists(picturesPath))
    {
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(picturesPath),
            RequestPath = "/pictures",
            // Same reasoning as above. These are user-supplied images, so a
            // replaced picture must not go on being served from cache either.
            OnPrepareResponse = ctx =>
            {
                ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate";
            }
        });
    }
    app.UseWebSockets();
    app.UseRouting();
    app.UseAuthorization();
    //app.MapGet("/", () => "ROOT ROUTE HIT");

    app.MapRazorPages();
    app.MapControllers();

    // MAP SIGNALR HUB:
    app.MapHub<Yaesu_Web_Control.Hubs.RadioHub>("/radioHub");

    // Remote audio WebSocket (binary Opus/PCM frames) — not SignalR.
    app.Map("/audio", async (HttpContext ctx, Yaesu_Web_Control.Services.Audio.RadioAudioBridgeService bridge) =>
    {
        await bridge.HandleWebSocketAsync(ctx);
    });

    app.MapGet("/api/status/init", () => new { status = Yaesu_Web_Control.Services.AppStatus.InitializationStatus });

    app.MapGet("/api/ports", () =>
    {
        var ports = System.IO.Ports.SerialPort.GetPortNames();
        return new { ports, com6Present = ports.Contains("COM6") };
    });

    // Serve accessible labels from AppData — copy default on first run so users can find and edit it.
    app.MapGet("/i18n/labels.json", (IWebHostEnvironment env) =>
    {
        var userPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Yaesu Web Control", "labels.json");

        if (!File.Exists(userPath))
        {
            var defaultPath = Path.Combine(env.WebRootPath, "i18n", "labels.default.json");
            Directory.CreateDirectory(Path.GetDirectoryName(userPath)!);
            File.Copy(defaultPath, userPath);
        }

        return Results.File(userPath, "application/json");
    });

#if WINDOWS
    app.MapPost("/api/sdr/span", async (
        [Microsoft.AspNetCore.Mvc.FromQuery] double hz,
        [Microsoft.AspNetCore.Mvc.FromQuery] string? sdrId,
        Yaesu_Web_Control.Services.ISettingsService settings,
        Yaesu_Web_Control.Services.Sdr.SdrManager sdr) =>
    {
        double[] valid = [62_500, 125_000, 250_000, 500_000, 1_000_000, 2_000_000];
        if (Array.IndexOf(valid, hz) < 0) return Results.BadRequest("Invalid span value.");

        // sdrId defaults to "A" for backward compatibility with any caller
        // that doesn't supply it. v2.3.0+ frontend always sends an explicit
        // "A" or "B"; older code paths (or third-party clients) get the
        // single-SDR behaviour.
        var target = (sdrId ?? "A").ToUpperInvariant();
        if (target != "A" && target != "B") return Results.BadRequest("sdrId must be A or B.");

        var s = await settings.GetSettingsAsync();
        if (target == "A") s.SdrSampleRateHzA = hz;
        else               s.SdrSampleRateHzB = hz;
        await settings.SaveSettingsAsync(s);
        sdr.RequestRestart();
        return Results.Ok();
    });
#endif

    // Open browser automatically when app starts (but not when debugging in Visual Studio)
    var browserLauncher = app.Services.GetRequiredService<BrowserLauncher>();
    var portInfo        = app.Services.GetRequiredService<HttpPortInfo>();
    var lifetime        = app.Services.GetRequiredService<IHostApplicationLifetime>();

    // Lifecycle-event log fences so we can see in the Serilog file exactly
    // when each shutdown phase fires. Helps diagnose "what's the framework
    // doing for 30 s between ApplicationStopping and the first hosted-service
    // StopAsync" — see project todo memory.
    lifetime.ApplicationStopping.Register(() => Log.Information("[Lifecycle] ApplicationStopping fired"));
    lifetime.ApplicationStopped.Register(()  => Log.Information("[Lifecycle] ApplicationStopped fired"));

    lifetime.ApplicationStarted.Register(() =>
    {
        browserLauncher.OpenOnce(portInfo.RootUrl);
    });

#if !WINDOWS
    if (OperatingSystem.IsMacOS())
    {
        // AppKit/Avalonia must own the process main thread. Start Kestrel first,
        // then block here on the menu-bar dispatcher until Exit / StopApplication.
        await app.StartAsync();
        Log.Information("[Lifecycle] Kestrel started; entering macOS tray main loop");
        try
        {
            app.Services.GetRequiredService<MacSystemTrayService>()
                .RunBlocking(lifetime.ApplicationStopping);
        }
        finally
        {
            Log.Information("[Lifecycle] macOS tray loop ended — stopping host");
            // Avalonia's MainLoop installs AvaloniaSynchronizationContext on this
            // thread. After MainLoop returns the dispatcher is no longer pumping,
            // so awaiting StopAsync here posts its continuations onto a dead
            // queue and hangs forever — Ctrl+C / tray Exit never finish (logs
            // stop at "tray loop ended"; only kill works). Clear the context
            // and run host shutdown on the thread pool.
            SynchronizationContext.SetSynchronizationContext(null);
            await Task.Run(() => app.StopAsync()).ConfigureAwait(false);
        }
    }
    else
    {
        app.Run();
    }
#else
    app.Run();
#endif
    Log.Information("app.Run() returned cleanly — flushing logs and exiting");
    Log.CloseAndFlush();
}
catch (Exception ex)
{
    var msg = $"[FATAL] Application failed to start: {ex.Message}\n{ex.StackTrace}";
    Console.Error.WriteLine(msg);
    try { File.AppendAllText("fatal_startup_error.log", $"{DateTime.Now:u} {msg}\n"); } catch { }
    Log.Fatal(ex, "Application failed to start");
    Log.CloseAndFlush();

    if (IsPortInUseException(ex))
    {
        // We pre-probed the port before configuring Kestrel, so this catch is
        // only reached if the chosen port was grabbed by another process in
        // the race window between probe and bind. Report whichever port we
        // actually chose, not the hardcoded default.
        var owner = GetPortOwner(chosenPort);
        var portMsg = owner is not null
            ? $"Port {chosenPort} is already in use by {owner}.\n\nClose that application and try again."
            : $"Port {chosenPort} is already in use by another application.\n\nClose that application and try again.";
#if WINDOWS
#pragma warning disable CA1416
        MessageBox.Show(portMsg, "Port In Use", MessageBoxButtons.OK, MessageBoxIcon.Error);
#pragma warning restore CA1416
#else
        Console.Error.WriteLine(portMsg);
#endif
    }
    else
    {
#if WINDOWS
#pragma warning disable CA1416
        MessageBox.Show(
            $"Yaesu Web Control failed to start:\n\n{ex.Message}",
            "Startup Error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
#pragma warning restore CA1416
#else
        Console.Error.WriteLine($"Yaesu Web Control failed to start:\n\n{ex.Message}");
#endif
    }

    throw;
}

#if WINDOWS
// Win32 P/Invokes used during YWC startup. Kept at the end of Program.cs
// rather than scattered through the top-level statements so the bootstrap
// logic stays readable.
internal static class NativeWin32
{
    /// <summary>
    /// The system does not display the critical-error-handler message box.
    /// Failing DLL loads return an error code to the caller instead of
    /// showing the "Entry Point Not Found" / "DLL was not found" dialogs.
    /// </summary>
    public const uint SEM_FAILCRITICALERRORS = 0x0001;

    /// <summary>
    /// The OpenFile function does not display a message box when it fails
    /// to find a file. Belt-and-braces alongside SEM_FAILCRITICALERRORS.
    /// </summary>
    public const uint SEM_NOOPENFILEERRORBOX = 0x8000;

    [DllImport("kernel32.dll")]
    public static extern uint SetErrorMode(uint uMode);
}
#endif

