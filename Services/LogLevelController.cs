using System.Text.Json;
using Serilog.Core;
using Serilog.Events;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Owns the Serilog minimum-level switch behind the "Detailed logging
    /// (for bug reports)" setting.
    ///
    /// Normal (default) is Information: startup, connections, mode/band
    /// changes, warnings and errors — roughly a megabyte a day, and enough to
    /// answer "what was it doing when it broke" for a fault the user could not
    /// reproduce on demand (issue #73's intermittent startup hang was
    /// diagnosed entirely from an unrequested log). Detailed drops to Debug,
    /// which restores the per-command CAT traffic, every received reply, each
    /// state save and each browser status poll — about 85,000 extra lines a
    /// day on a live radio.
    ///
    /// Held in a <see cref="LoggingLevelSwitch"/> rather than baked into the
    /// logger so the checkbox takes effect immediately. Requiring a restart to
    /// start logging would destroy the state that caused the bug.
    /// </summary>
    public static class LogLevelController
    {
        /// <summary>
        /// Wired into the logger via <c>MinimumLevel.ControlledBy</c> in Program.cs.
        /// Starts at Information; Program.cs applies the saved setting before the
        /// first log line is written.
        /// </summary>
        public static readonly LoggingLevelSwitch Switch = new(LogEventLevel.Information);

        /// <summary>Currently logging at Debug.</summary>
        public static bool IsDetailed => Switch.MinimumLevel <= LogEventLevel.Debug;

        public static void Apply(bool detailed) =>
            Switch.MinimumLevel = detailed ? LogEventLevel.Debug : LogEventLevel.Information;

        /// <summary>
        /// Reads the flag straight out of appsettings.user.json.
        ///
        /// Program.cs configures Serilog before the DI container exists, so
        /// ISettingsService is not available yet and we cannot wait for it —
        /// a user who ticked the box wants the startup sequence captured, and
        /// that is over before the container is built. Deliberately tolerant:
        /// any missing file, malformed JSON or absent property means "normal",
        /// because failing to read one optional flag must never stop the app
        /// starting.
        /// </summary>
        public static bool ReadDetailedLoggingFromDisk()
        {
            try
            {
                // Mirrors SettingsService's path. ApplicationData resolves to
                // %APPDATA% on Windows and $XDG_CONFIG_HOME (or ~/.config) on
                // macOS/Linux, so this is correct on every host.
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MM5AGM", "Yaesu Web Control", "appsettings.user.json");

                if (!File.Exists(path)) return false;

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.TryGetProperty(nameof(Models.ApplicationSettings.DetailedLogging), out var el)
                    && el.ValueKind == JsonValueKind.True;
            }
            catch
            {
                return false;
            }
        }
    }
}
