using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yaesu_Web_Control.Models;

namespace Yaesu_Web_Control.Services.Flex
{
    /// <summary>
    /// Singleton implementation of <see cref="IFlexLayoutStore"/>, owning
    /// <c>flex-layouts.json</c> in <c>%APPDATA%\MM5AGM\Yaesu Web Control\</c>
    /// (or <c>$XDG_CONFIG_HOME</c> on macOS/Linux).
    ///
    /// Deliberately a separate file from <c>appsettings.user.json</c>: the
    /// Settings page rewrites the whole settings object from a form read-modify-write,
    /// so storing layouts there would let a Settings save clobber a layout saved
    /// from the Flex UI in the same window.
    ///
    /// Thread safety mirrors <c>VCTuneConfigurationStore</c>: an immutable
    /// <see cref="State"/> snapshot is swapped atomically for reads, while a
    /// <see cref="SemaphoreSlim"/> serialises the read-modify-write + disk write
    /// path. Writes are atomic (temp file then replace), so a crash mid-write
    /// cannot leave a truncated layout file.
    /// </summary>
    public sealed class FlexLayoutStore : IFlexLayoutStore
    {
        public const int MaxLayouts = 50;
        public const int MaxNameLength = 40;
        public const int MaxLayoutBytes = 256 * 1024;
        private const int CurrentStoreVersion = 1;

        private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private readonly string _filePath;
        private readonly ILogger<FlexLayoutStore> _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private volatile State _state;

        public FlexLayoutStore(ILogger<FlexLayoutStore> logger)
            : this(logger, null)
        {
        }

        /// <summary>
        /// Test/embedding seam: pass an explicit file path. Production resolves
        /// the standard per-user data directory when <paramref name="filePath"/>
        /// is null.
        /// </summary>
        public FlexLayoutStore(ILogger<FlexLayoutStore> logger, string? filePath)
        {
            _logger = logger;
            _filePath = string.IsNullOrWhiteSpace(filePath)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MM5AGM", "Yaesu Web Control", "flex-layouts.json")
                : filePath;

            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            _state = ReadFromDisk();
        }

        // ── Reads ────────────────────────────────────────────────────────────

        public FlexLayoutSnapshot GetSnapshot()
        {
            var state = _state;
            return new FlexLayoutSnapshot
            {
                ActiveId = state.ActiveId,
                Layouts = state.Layouts.Values
                    .OrderBy(l => l.CreatedAt)
                    .Select(ToSummary)
                    .ToList(),
            };
        }

        public FlexLayoutDetail? Get(string id)
        {
            if (string.IsNullOrEmpty(id) || !_state.Layouts.TryGetValue(id, out var stored))
                return null;

            return new FlexLayoutDetail
            {
                Id = stored.Id,
                Name = stored.Name,
                Layout = stored.Layout,
                CreatedAt = stored.CreatedAt,
                UpdatedAt = stored.UpdatedAt,
            };
        }

        // ── Writes ───────────────────────────────────────────────────────────

        public async Task<FlexLayoutMutation> CreateAsync(
            string id, string name, JsonNode? layout, CancellationToken ct = default)
        {
            id ??= "";
            if (!IdPattern.IsMatch(id) || id == FlexLayoutDefaults.DefaultId)
                return Invalid("Layout id is missing or invalid.");

            var cleanName = (name ?? "").Trim();
            if (cleanName.Length is < 1 or > MaxNameLength)
                return Invalid($"Layout name must be 1–{MaxNameLength} characters.");

            if (!IsAcceptableLayout(layout, out var layoutError))
                return Invalid(layoutError!);

            await _gate.WaitAsync(ct);
            try
            {
                var state = _state;
                if (state.Layouts.ContainsKey(id))
                    return new FlexLayoutMutation(FlexLayoutMutationStatus.Conflict,
                        Error: "A layout with that id already exists.");
                if (state.Layouts.Count >= MaxLayouts)
                    return new FlexLayoutMutation(FlexLayoutMutationStatus.CapReached,
                        Error: $"At most {MaxLayouts} layouts are supported.");

                var now = DateTimeOffset.UtcNow;
                var stored = new StoredLayout(id, cleanName, layout!.DeepClone(), now, now);
                var next = new State
                {
                    Layouts = new Dictionary<string, StoredLayout>(state.Layouts, StringComparer.Ordinal)
                    {
                        [id] = stored,
                    },
                    ActiveId = state.ActiveId,
                };

                await PersistAsync(next, ct);
                _state = next;
                return new FlexLayoutMutation(FlexLayoutMutationStatus.Ok, ToSummary(stored));
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<FlexLayoutMutation> UpdateAsync(
            string id, string? name, JsonNode? layout, DateTimeOffset? expectedUpdatedAt = null,
            CancellationToken ct = default)
        {
            if (id == FlexLayoutDefaults.DefaultId)
                return new FlexLayoutMutation(FlexLayoutMutationStatus.Protected,
                    Error: "The built-in default layout cannot be modified.");

            var cleanName = name?.Trim();
            if (cleanName is { Length: 0 })
                return Invalid("Layout name cannot be empty.");
            if (cleanName is { Length: > MaxNameLength })
                return Invalid($"Layout name must be {MaxNameLength} characters or fewer.");

            if (layout is not null && !IsAcceptableLayout(layout, out var layoutError))
                return Invalid(layoutError!);

            await _gate.WaitAsync(ct);
            try
            {
                var state = _state;
                if (!state.Layouts.TryGetValue(id, out var existing))
                    return new FlexLayoutMutation(FlexLayoutMutationStatus.NotFound,
                        Error: "Layout not found.");

                if (expectedUpdatedAt is { } expected && existing.UpdatedAt != expected)
                    return new FlexLayoutMutation(FlexLayoutMutationStatus.Conflict,
                        Error: "The layout was changed elsewhere.");

                var updated = existing with
                {
                    Name = cleanName ?? existing.Name,
                    Layout = layout is null ? existing.Layout : layout.DeepClone(),
                    UpdatedAt = DateTimeOffset.UtcNow,
                };

                var next = new State
                {
                    Layouts = new Dictionary<string, StoredLayout>(state.Layouts, StringComparer.Ordinal)
                    {
                        [id] = updated,
                    },
                    ActiveId = state.ActiveId,
                };

                await PersistAsync(next, ct);
                _state = next;
                return new FlexLayoutMutation(FlexLayoutMutationStatus.Ok, ToSummary(updated));
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<FlexLayoutMutation> DeleteAsync(string id, CancellationToken ct = default)
        {
            if (id == FlexLayoutDefaults.DefaultId)
                return new FlexLayoutMutation(FlexLayoutMutationStatus.Protected,
                    Error: "The built-in default layout cannot be deleted.");

            await _gate.WaitAsync(ct);
            try
            {
                var state = _state;
                if (!state.Layouts.TryGetValue(id, out var existing))
                    return new FlexLayoutMutation(FlexLayoutMutationStatus.NotFound,
                        Error: "Layout not found.");

                var layouts = new Dictionary<string, StoredLayout>(state.Layouts, StringComparer.Ordinal);
                layouts.Remove(id);

                var next = new State
                {
                    Layouts = layouts,
                    ActiveId = state.ActiveId == id ? FlexLayoutDefaults.DefaultId : state.ActiveId,
                };

                await PersistAsync(next, ct);
                _state = next;
                return new FlexLayoutMutation(FlexLayoutMutationStatus.Ok, ToSummary(existing));
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<FlexLayoutMutation> SetActiveAsync(string id, CancellationToken ct = default)
        {
            id ??= "";
            if (id != FlexLayoutDefaults.DefaultId && !IdPattern.IsMatch(id))
                return new FlexLayoutMutation(FlexLayoutMutationStatus.NotFound,
                    Error: "Layout not found.");

            await _gate.WaitAsync(ct);
            try
            {
                var state = _state;
                if (id != FlexLayoutDefaults.DefaultId && !state.Layouts.ContainsKey(id))
                    return new FlexLayoutMutation(FlexLayoutMutationStatus.NotFound,
                        Error: "Layout not found.");
                if (state.ActiveId == id)
                    return new FlexLayoutMutation(FlexLayoutMutationStatus.Ok);

                var next = new State { Layouts = state.Layouts, ActiveId = id };
                await PersistAsync(next, ct);
                _state = next;
                return new FlexLayoutMutation(FlexLayoutMutationStatus.Ok);
            }
            finally
            {
                _gate.Release();
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static bool IsAcceptableLayout(JsonNode? layout, out string? error)
        {
            if (layout is null)
            {
                error = "A layout payload is required.";
                return false;
            }
            if (layout is not JsonObject)
            {
                error = "Layout payload must be a JSON object.";
                return false;
            }
            if (Encoding.UTF8.GetByteCount(layout.ToJsonString()) > MaxLayoutBytes)
            {
                error = $"Layout payload exceeds {MaxLayoutBytes / 1024} KB.";
                return false;
            }
            error = null;
            return true;
        }

        private static FlexLayoutMutation Invalid(string message) =>
            new(FlexLayoutMutationStatus.Invalid, Error: message);

        private static FlexLayoutSummary ToSummary(StoredLayout l) => new()
        {
            Id = l.Id,
            Name = l.Name,
            CreatedAt = l.CreatedAt,
            UpdatedAt = l.UpdatedAt,
        };

        private State ReadFromDisk()
        {
            try
            {
                if (!File.Exists(_filePath)) return State.Empty;

                var json = File.ReadAllText(_filePath);
                var doc = JsonSerializer.Deserialize<PersistedStore>(json, JsonOptions);
                if (doc is null) return State.Empty;

                if (doc.StoreVersion > CurrentStoreVersion)
                {
                    // A newer build wrote this. Keep every record we understand
                    // rather than discarding the user's layouts on a downgrade.
                    _logger.LogWarning(
                        "[FlexLayoutStore] {Path} has storeVersion {Found} newer than {Current}; loading known fields only.",
                        _filePath, doc.StoreVersion, CurrentStoreVersion);
                }

                var layouts = new Dictionary<string, StoredLayout>(StringComparer.Ordinal);
                foreach (var l in doc.Layouts)
                {
                    if (string.IsNullOrWhiteSpace(l.Id) || !IdPattern.IsMatch(l.Id)
                        || l.Id == FlexLayoutDefaults.DefaultId)
                        continue;

                    var stored = new StoredLayout(
                        l.Id, (l.Name ?? "").Trim(), l.Layout, l.CreatedAt, l.UpdatedAt);
                    if (!layouts.TryAdd(l.Id, stored))
                        _logger.LogWarning(
                            "[FlexLayoutStore] Duplicate layout id {Id} in {Path}; keeping the first.",
                            l.Id, _filePath);
                }

                var active = doc.ActiveId;
                if (active != FlexLayoutDefaults.DefaultId && !layouts.ContainsKey(active))
                    active = FlexLayoutDefaults.DefaultId;

                return new State { Layouts = layouts, ActiveId = active };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FlexLayoutStore] Failed to read {Path}; starting empty.", _filePath);
                return State.Empty;
            }
        }

        private async Task PersistAsync(State state, CancellationToken ct)
        {
            var doc = new PersistedStore
            {
                StoreVersion = CurrentStoreVersion,
                ActiveId = state.ActiveId,
                Layouts = state.Layouts.Values
                    .OrderBy(l => l.CreatedAt)
                    .Select(l => new PersistedLayout
                    {
                        Id = l.Id,
                        Name = l.Name,
                        Layout = l.Layout,
                        CreatedAt = l.CreatedAt,
                        UpdatedAt = l.UpdatedAt,
                    })
                    .ToList(),
            };

            var json = JsonSerializer.Serialize(doc, JsonOptions);
            var tmp = _filePath + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct);
            File.Move(tmp, _filePath, overwrite: true);
        }

        // ── State ────────────────────────────────────────────────────────────

        private sealed class State
        {
            public required IReadOnlyDictionary<string, StoredLayout> Layouts { get; init; }
            public required string ActiveId { get; init; }

            public static State Empty => new()
            {
                Layouts = new Dictionary<string, StoredLayout>(StringComparer.Ordinal),
                ActiveId = FlexLayoutDefaults.DefaultId,
            };
        }

        private sealed record StoredLayout(
            string Id,
            string Name,
            JsonNode? Layout,
            DateTimeOffset CreatedAt,
            DateTimeOffset UpdatedAt);

        // Mutable POCOs for the on-disk shape (System.Text.Json needs settable
        // members on read). The in-memory State stays immutable.
        private sealed class PersistedStore
        {
            public int StoreVersion { get; set; } = CurrentStoreVersion;
            public string ActiveId { get; set; } = FlexLayoutDefaults.DefaultId;
            public List<PersistedLayout> Layouts { get; set; } = new();
        }

        private sealed class PersistedLayout
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public JsonNode? Layout { get; set; }
            public DateTimeOffset CreatedAt { get; set; }
            public DateTimeOffset UpdatedAt { get; set; }
        }
    }
}
