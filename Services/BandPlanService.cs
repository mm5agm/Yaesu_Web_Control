using System.Text.Json;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// One band's edges for one region, in Hz. Mirrors the { name, lo, hi }
    /// shape used by <c>wwwroot/bandplan.default.json</c> and by BAND_EDGES in
    /// <c>wwwroot/js/ui/band-plan.js</c> — the same file feeds both sides, so
    /// server and browser can no longer disagree about where a band ends.
    /// </summary>
    public sealed class BandEdge
    {
        public string Name { get; set; } = "";
        public long Lo { get; set; }
        public long Hi { get; set; }
    }

    public interface IBandPlanService
    {
        /// <summary>Normalised IARU region currently configured: "Region1".."Region3" or "Japan".</summary>
        string CurrentRegion { get; }

        /// <summary>Band edges for the configured region, lowest band first.</summary>
        IReadOnlyList<BandEdge> CurrentEdges { get; }

        /// <summary>
        /// Band name ("20m") for a frequency, or "Unknown" when the frequency
        /// falls outside every allocation in the operator's own region.
        /// </summary>
        string BandForFrequency(long hz);
    }

    /// <summary>
    /// Resolves frequencies to band names using the band edges of the operator's
    /// configured IARU region (Settings → Band Plan / IARU Region).
    ///
    /// Before this existed, RadioStateService carried its own hardcoded ladder
    /// that was region-blind, and it disagreed with the browser's region-aware
    /// BAND_EDGES: a UK operator on 3.9 MHz was told "80m" by the server while
    /// the waterfall correctly showed them outside the Region 1 allocation.
    /// There is now one table — <c>wwwroot/bandplan.default.json</c> — read by
    /// both, resolved to a single region here at the seam.
    ///
    /// All regions stay in the JSON; "only the operator's region" means
    /// *resolved* to one, not *shipped* as one, so switching region in Settings
    /// keeps working.
    /// </summary>
    public sealed class BandPlanService : IBandPlanService
    {
        public const string UnknownBand = "Unknown";
        private const string DefaultRegion = "Region1";

        // The configured region is re-read from settings at most this often.
        // FrequencyA/B setters call in here on every CAT frequency update
        // (~10 Hz), and SettingsService.GetSettingsAsync re-reads the file on
        // every call, so an uncached lookup would be a file read per update.
        // Five seconds means a region change in Settings takes effect well
        // before the operator can get back to the main page.
        private const long RegionTtlMs = 5000;

        private readonly IWebHostEnvironment _env;
        private readonly ISettingsService _settings;
        private readonly ILogger<BandPlanService> _logger;
        private readonly object _edgesLock = new();

        private Dictionary<string, List<BandEdge>>? _edgesByRegion;
        private string _region = DefaultRegion;
        private long _regionCheckedAt = long.MinValue;

        public BandPlanService(
            IWebHostEnvironment env,
            ISettingsService settings,
            ILogger<BandPlanService> logger)
        {
            _env = env;
            _settings = settings;
            _logger = logger;
        }

        public string CurrentRegion => ResolveRegion();

        public IReadOnlyList<BandEdge> CurrentEdges => EdgesFor(ResolveRegion());

        public string BandForFrequency(long hz)
        {
            if (hz <= 0) return UnknownBand;

            foreach (var edge in EdgesFor(ResolveRegion()))
            {
                if (hz >= edge.Lo && hz <= edge.Hi)
                    return edge.Name;
            }
            return UnknownBand;
        }

        // ── Region ──────────────────────────────────────────────────────────

        /// <summary>Legacy setting values map onto the IARU region names.
        /// Pages/Index.cshtml.cs and MemoryController do the same mapping.</summary>
        public static string NormaliseRegion(string? bandPlan) => bandPlan switch
        {
            "UK" => "Region1",
            "USA" => "Region2",
            "Region1" or "Region2" or "Region3" or "Japan" => bandPlan,
            _ => DefaultRegion
        };

        private string ResolveRegion()
        {
            var now = Environment.TickCount64;
            if (now - Volatile.Read(ref _regionCheckedAt) < RegionTtlMs)
                return Volatile.Read(ref _region);

            Volatile.Write(ref _regionCheckedAt, now);
            try
            {
                // Sync-over-async, deliberately: the settings cache is behind an
                // async API but this sits inside a synchronous property setter
                // chain. Rate-limited by the TTL above to well under the 2 Hz
                // the meter poll already does. Same pattern as CalibrationStorage.
                //
                // Task.Run rather than a bare .GetAwaiter().GetResult(): YWC runs
                // a WinForms message loop (the WinForms host that owns Kestrel),
                // so a caller could arrive on a thread carrying a WinForms
                // SynchronizationContext. Blocking there while GetSettingsAsync's
                // file read tries to resume on that same thread would deadlock.
                // Task.Run moves the continuation onto the thread pool, where it
                // cannot.
                var configured = Task.Run(() => _settings.GetSettingsAsync())
                                     .GetAwaiter().GetResult().BandPlan;
                var resolved = NormaliseRegion(configured);
                if (!string.Equals(resolved, Volatile.Read(ref _region), StringComparison.Ordinal))
                {
                    _logger.LogInformation("[BandPlan] Region is now {Region} (setting: {Setting})",
                        resolved, configured ?? "(null)");
                    Volatile.Write(ref _region, resolved);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[BandPlan] Could not read the configured region; keeping {Region}",
                    Volatile.Read(ref _region));
            }
            return Volatile.Read(ref _region);
        }

        // ── Edges ───────────────────────────────────────────────────────────

        private IReadOnlyList<BandEdge> EdgesFor(string region)
        {
            var all = LoadEdges();
            if (all.TryGetValue(region, out var edges)) return edges;
            if (all.TryGetValue(DefaultRegion, out var fallback)) return fallback;
            return Array.Empty<BandEdge>();
        }

        private Dictionary<string, List<BandEdge>> LoadEdges()
        {
            var cached = Volatile.Read(ref _edgesByRegion);
            if (cached != null) return cached;

            lock (_edgesLock)
            {
                if (_edgesByRegion != null) return _edgesByRegion;
                _edgesByRegion = ReadEdgesFromDisk() ?? BuiltInEdges();
                return _edgesByRegion;
            }
        }

        /// <summary>
        /// Read bandEdges from wwwroot/bandplan.default.json — the same file the
        /// browser overlays at startup, so an operator who drops in a corrected
        /// copy fixes both sides at once. Returns null on any problem so the
        /// built-in table takes over.
        /// </summary>
        private Dictionary<string, List<BandEdge>>? ReadEdgesFromDisk()
        {
            try
            {
                var webRoot = _env.WebRootPath;
                if (string.IsNullOrEmpty(webRoot)) return null;

                var path = Path.Combine(webRoot, "bandplan.default.json");
                if (!File.Exists(path))
                {
                    _logger.LogWarning("[BandPlan] {Path} not found; using the built-in band edges", path);
                    return null;
                }

                var file = JsonSerializer.Deserialize<BandPlanFile>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                var edges = file?.BandEdges;
                if (edges == null || edges.Count == 0)
                {
                    _logger.LogWarning("[BandPlan] {Path} has no bandEdges; using the built-in band edges", path);
                    return null;
                }

                foreach (var list in edges.Values)
                    list.Sort((a, b) => a.Lo.CompareTo(b.Lo));

                _logger.LogInformation("[BandPlan] Loaded band edges for {Count} region(s) from {Path}",
                    edges.Count, path);
                return edges;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BandPlan] Failed to read bandplan.default.json; using the built-in band edges");
                return null;
            }
        }

        private sealed class BandPlanFile
        {
            public Dictionary<string, List<BandEdge>>? BandEdges { get; set; }
        }

        /// <summary>
        /// Shipped fallback, identical to BAND_EDGES in band-plan.js. Only used
        /// when the JSON is missing or unreadable — the JSON is the source of
        /// truth, this is what keeps the app working without it.
        /// </summary>
        private static Dictionary<string, List<BandEdge>> BuiltInEdges() => new()
        {
            ["Region1"] = new()
            {
                new() { Name = "160m", Lo =  1810000, Hi =  2000000 },
                new() { Name =  "80m", Lo =  3500000, Hi =  3800000 },
                new() { Name =  "60m", Lo =  5351500, Hi =  5366500 },
                new() { Name =  "40m", Lo =  7000000, Hi =  7200000 },
                new() { Name =  "30m", Lo = 10100000, Hi = 10150000 },
                new() { Name =  "20m", Lo = 14000000, Hi = 14350000 },
                new() { Name =  "17m", Lo = 18068000, Hi = 18168000 },
                new() { Name =  "15m", Lo = 21000000, Hi = 21450000 },
                new() { Name =  "12m", Lo = 24890000, Hi = 24990000 },
                new() { Name =  "10m", Lo = 28000000, Hi = 29700000 },
                new() { Name =   "6m", Lo = 50000000, Hi = 52000000 },
                new() { Name =   "4m", Lo = 70000000, Hi = 70500000 },
            },
            ["Region2"] = new()
            {
                new() { Name = "160m", Lo =  1800000, Hi =  2000000 },
                new() { Name =  "80m", Lo =  3500000, Hi =  4000000 },
                new() { Name =  "60m", Lo =  5330500, Hi =  5403500 },
                new() { Name =  "40m", Lo =  7000000, Hi =  7300000 },
                new() { Name =  "30m", Lo = 10100000, Hi = 10150000 },
                new() { Name =  "20m", Lo = 14000000, Hi = 14350000 },
                new() { Name =  "17m", Lo = 18068000, Hi = 18168000 },
                new() { Name =  "15m", Lo = 21000000, Hi = 21450000 },
                new() { Name =  "12m", Lo = 24890000, Hi = 24990000 },
                new() { Name =  "10m", Lo = 28000000, Hi = 29700000 },
                new() { Name =   "6m", Lo = 50000000, Hi = 54000000 },
            },
            ["Region3"] = new()
            {
                new() { Name = "160m", Lo =  1800000, Hi =  2000000 },
                new() { Name =  "80m", Lo =  3500000, Hi =  3900000 },
                new() { Name =  "60m", Lo =  5351500, Hi =  5366500 },
                new() { Name =  "40m", Lo =  7000000, Hi =  7300000 },
                new() { Name =  "30m", Lo = 10100000, Hi = 10150000 },
                new() { Name =  "20m", Lo = 14000000, Hi = 14350000 },
                new() { Name =  "17m", Lo = 18068000, Hi = 18168000 },
                new() { Name =  "15m", Lo = 21000000, Hi = 21450000 },
                new() { Name =  "12m", Lo = 24890000, Hi = 24990000 },
                new() { Name =  "10m", Lo = 28000000, Hi = 29700000 },
                new() { Name =   "6m", Lo = 50000000, Hi = 54000000 },
            },
            ["Japan"] = new()
            {
                // 160m and 80m are fragmented in Japan; these are envelopes
                // spanning the sub-bands, matching band-plan.js.
                new() { Name = "160m", Lo =  1810000, Hi =  1912500 },
                new() { Name =  "80m", Lo =  3500000, Hi =  3805000 },
                new() { Name =  "40m", Lo =  7000000, Hi =  7200000 },
                new() { Name =  "30m", Lo = 10100000, Hi = 10150000 },
                new() { Name =  "20m", Lo = 14000000, Hi = 14350000 },
                new() { Name =  "17m", Lo = 18068000, Hi = 18168000 },
                new() { Name =  "15m", Lo = 21000000, Hi = 21450000 },
                new() { Name =  "12m", Lo = 24890000, Hi = 24990000 },
                new() { Name =  "10m", Lo = 28000000, Hi = 29700000 },
                new() { Name =   "6m", Lo = 50000000, Hi = 54000000 },
            },
        };
    }
}
