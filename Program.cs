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
using System.Windows.Forms;

// ── Single-instance guard ────────────────────────────────────────────────────
const string MutexName = "Global\\Yaesu_Web_Control_SingleInstance";
var mutex = new Mutex(initiallyOwned: true, name: MutexName, out bool createdNew);

if (!createdNew)
{
#pragma warning disable CA1416
    MessageBox.Show(
        "Yaesu Web Control is already running.",
        "Already Running",
        MessageBoxButtons.OK,
        MessageBoxIcon.Information);
#pragma warning restore CA1416

    mutex.Dispose();
    return;
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

// ── SoapySDR native library resolver ────────────────────────────────────────
// .NET P/Invoke on Windows does not search PATH directories by default.
// Resolve SoapySDR.dll explicitly from its install location so the P/Invoke
// declarations in SoapySdrInterop are satisfied without relying on PATH.
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
        return IntPtr.Zero;   // fall back to default resolution for all other DLLs
    });

// ── Serilog file logging ────────────────────────────────────────────────────
// YWC is a WinExe (no console window) so stdout-based loggers are invisible.
// Wire up Serilog with a rolling-daily file sink under %APPDATA% so we have a
// readable record of what the app did — essential for diagnosing shutdown
// hangs, CAT timeouts, SDR init failures and anything else the user can't see.
var logDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "MM5AGM", "Yaesu Web Control", "logs");
try { Directory.CreateDirectory(logDir); } catch { /* fall through, Serilog will surface the problem */ }

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore.SignalR", Serilog.Events.LogEventLevel.Warning)
    // Keep Hosting.Lifetime at Information so we see exactly when
    // StopApplication is called and when each hosted service's StopAsync runs
    // — invaluable for diagnosing shutdown stalls.
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.File(
        Path.Combine(logDir, "ywc-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        shared: true,
        flushToDiskInterval: TimeSpan.FromSeconds(1),
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

Log.Information("Yaesu Web Control starting (v{Version})", Yaesu_Web_Control.AppVersion.Current);

var builder = WebApplication.CreateBuilder(args);

// Cap the host's overall shutdown timeout. Default is 30 s (which we hit on
// every tray Exit before adding this cap); 2 s is plenty for our user
// services to wind down their StopAsync routines. Tracked in the project
// todo memory.
builder.Services.Configure<HostOptions>(opts =>
{
    opts.ShutdownTimeout = TimeSpan.FromSeconds(2);
});

builder.Services.AddSingleton<CalibrationStorage>();
builder.Services.AddSingleton<ICalibrationService, CalibrationService>();

// ADD SIGNALR EARLY (before services that depend on IHubContext):
builder.Services.AddSignalR();

// Register the persistence service (no hub dependency)
builder.Services.AddSingleton<RadioStatePersistenceService>();

// Register RadioStateService and CatMessageBuffer as singletons
builder.Services.AddSingleton<RadioStateService>();
builder.Services.AddSingleton<CatMessageBuffer>();

// Register CatMessageDispatcher as singleton
builder.Services.AddSingleton<CatMessageDispatcher>();

// Register CatMultiplexerService as singleton
builder.Services.AddSingleton<CatMultiplexerService>();

// Register the main CAT client for the web app
builder.Services.AddSingleton<ICatClient, MultiplexedCatClient>();

// Register the rigctld server as a background service
builder.Services.AddHostedService<RigctldServer>();

// Register your settings service
builder.Services.AddSingleton<ISettingsService, SettingsService>();


// Add after existing service registrations
builder.Services.AddHostedService<MeterPollingService>();

// SDR spectrum display — reads IQ samples, computes FFT, broadcasts via SignalR
// Registered as singleton so the span-change API endpoint can call RequestRestart().
builder.Services.AddSingleton<Yaesu_Web_Control.Services.Sdr.SdrManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Yaesu_Web_Control.Services.Sdr.SdrManager>());

// Register the radio state service — reuse the same singleton instance as RadioStateService
builder.Services.AddSingleton<IRadioStateService>(sp => sp.GetRequiredService<RadioStateService>());

// Register the radio initialization service
builder.Services.AddSingleton<RadioInitializationService>();
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
#pragma warning disable CA1416
    var diag = string.Join("\n",
        triedPorts.Select(p => $"  {p,5} — {GetPortOwner(p) ?? "unknown / Windows-reserved"}"));
    MessageBox.Show(
        $"Yaesu Web Control couldn't find a free TCP port to listen on.\n\n" +
        $"Tried ports {triedPorts.First()}–{triedPorts.Last()}:\n\n{diag}\n\n" +
        $"Either close one of those programs, or open Yaesu Web Control's\n" +
        $"Settings page on a working installation and change the HttpPort\n" +
        $"value in %APPDATA%\\MM5AGM\\Yaesu Web Control\\appsettings.user.json\n" +
        $"to a free port (e.g. 9080), then restart.",
        "No free port available",
        MessageBoxButtons.OK,
        MessageBoxIcon.Error);
#pragma warning restore CA1416
    return;
}

// Force the web host to use the chosen port on all interfaces.
builder.WebHost.UseUrls($"http://0.0.0.0:{chosenPort}");

// Publish the chosen port so every consumer reads from one source of truth.
builder.Services.AddSingleton(new HttpPortInfo(chosenPort));

builder.Services.AddSingleton<BrowserLauncher>();
// System tray icon — gives operators a visible "YWC is running" indicator
// and a clean Exit menu. Implemented as an STA-threaded hosted service.
builder.Services.AddHostedService<SystemTrayService>();

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

    app.UseStaticFiles();
    var picturesPath = System.IO.Path.Combine(app.Environment.ContentRootPath, "pictures");
    if (System.IO.Directory.Exists(picturesPath))
    {
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(picturesPath),
            RequestPath = "/pictures"
        });
    }
    app.UseRouting();
    app.UseAuthorization();
    //app.MapGet("/", () => "ROOT ROUTE HIT");

    app.MapRazorPages();
    app.MapControllers();

    // MAP SIGNALR HUB:
    app.MapHub<Yaesu_Web_Control.Hubs.RadioHub>("/radioHub");

    app.MapGet("/api/status/init", () => new { status = Yaesu_Web_Control.Services.AppStatus.InitializationStatus });

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

    app.MapPost("/api/sdr/span", async (
        [Microsoft.AspNetCore.Mvc.FromQuery] double hz,
        [Microsoft.AspNetCore.Mvc.FromQuery] string? sdrId,
        Yaesu_Web_Control.Services.ISettingsService settings,
        Yaesu_Web_Control.Services.Sdr.SdrManager sdr) =>
    {
        double[] valid = [62_500, 125_000, 250_000, 500_000, 1_024_000, 2_048_000, 2_500_000, 3_200_000];
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

    app.Run();
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

#pragma warning disable CA1416
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
        MessageBox.Show(portMsg, "Port In Use", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
    else
    {
        MessageBox.Show(
            $"Yaesu Web Control failed to start:\n\n{ex.Message}",
            "Startup Error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
#pragma warning restore CA1416

    throw;
}

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

