using System.Text.Json;
using System.Text.Json.Serialization;
using Yaesu_Web_Control.Models;

namespace Yaesu_Web_Control.Services.Voice
{
    /// <summary>
    /// Reads and writes voice language packs. Storage is per-locale:
    /// %APPDATA%\MM5AGM\Yaesu Web Control\Grammars\&lt;culture&gt;\Commands.&lt;culture&gt;.json
    /// (+ a sibling Commands.&lt;culture&gt;.meta.json), per
    /// docs/VoiceControl/language-pack-manager-design.md §1.6. Multiple
    /// cultures can be installed side by side; which one is actually loaded
    /// into the live SAPI engine is a separate concern (§4) tracked by
    /// ApplicationSettings.VoiceActiveLocale.
    ///
    /// Falls back to built-in defaults when no pack exists for a locale, or
    /// the JSON version is older than the current schema (version 3
    /// introduced DecomposedCommand for SetMode/SetBand and merged
    /// Verbs+Connectors into Triggers for SetFrequency).
    /// </summary>
    public sealed class VoicePhraseStore
    {
        public const string DefaultCulture = "en-GB";

        private static readonly string AppDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Yaesu Web Control");

        private static readonly string LegacyFilePath = Path.Combine(AppDataRoot, "voice_phrases.json");
        private static readonly string GrammarsRoot = Path.Combine(AppDataRoot, "Grammars");

        private static string LocaleDir(string culture) => Path.Combine(GrammarsRoot, culture);
        private static string JsonPath(string culture) => Path.Combine(LocaleDir(culture), $"Commands.{culture}.json");
        private static string MetaPath(string culture) => Path.Combine(LocaleDir(culture), $"Commands.{culture}.meta.json");
        private static string SrgsPath(string culture) => Path.Combine(LocaleDir(culture), $"Commands.{culture}.srgs");
        private static string DraftPath(string culture) => Path.Combine(LocaleDir(culture), $"Commands.{culture}.draft.json");

        // §3.3 version history — every overwrite of an installed pack is
        // snapshotted first so a bad edit or import can be undone.
        private const int MaxHistorySnapshots = 5;
        private static string HistoryDir(string culture) => Path.Combine(LocaleDir(culture), "history");
        private static string SnapshotDir(string culture, string snapshotId) => Path.Combine(HistoryDir(culture), snapshotId);

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>True if a pack is already installed for this culture (used by the import preview's conflict check).</summary>
        public bool IsInstalled(string culture) => File.Exists(JsonPath(culture));

        /// <summary>
        /// Cultures with an installed pack on disk (Grammars\&lt;culture&gt;\Commands.&lt;culture&gt;.json
        /// present), used by the language switcher (§4.1). Does NOT include
        /// DefaultCulture unless it actually has a file on disk -- callers
        /// that want "en-GB is always selectable via BuildDefaults()" add it
        /// themselves, since that's a UI policy, not a storage fact.
        /// </summary>
        public IReadOnlyList<string> ListInstalledCultures()
        {
            if (!Directory.Exists(GrammarsRoot)) return Array.Empty<string>();
            return Directory.GetDirectories(GrammarsRoot)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name) && File.Exists(JsonPath(name!)))
                .Select(name => name!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public VoicePhrasesConfig Load(string culture = DefaultCulture)
        {
            var path = JsonPath(culture);

            // First run after upgrading from the single-file layout: migrate
            // the legacy voice_phrases.json into the new per-locale location
            // rather than silently discarding a user's customised phrases.
            if (!File.Exists(path) && string.Equals(culture, DefaultCulture, StringComparison.OrdinalIgnoreCase))
                MigrateLegacyFile();

            if (!File.Exists(path))
                return BuildDefaults();

            try
            {
                var json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<VoicePhrasesConfig>(json, _jsonOpts);
                // Version 3 changed SetMode/SetBand to DecomposedCommand and merged
                // Verbs+Connectors into Triggers for SetFrequency. Older files are
                // reset to defaults rather than partially migrated.
                if (config == null || config.Version < 6)
                    return BuildDefaults();
                return config;
            }
            catch
            {
                return BuildDefaults();
            }
        }

        public void Save(VoicePhrasesConfig config, string culture = DefaultCulture)
        {
            SnapshotBeforeOverwrite(culture);

            Directory.CreateDirectory(LocaleDir(culture));
            File.WriteAllText(JsonPath(culture), JsonSerializer.Serialize(config, _jsonOpts));

            // Regenerated from the same data on every save so it can never
            // drift from the JSON that actually drives recognition — see
            // VoiceGrammar.GenerateSrgs and docs/VoiceControl/language-pack-manager-design.md §2.4.
            File.WriteAllText(SrgsPath(culture), VoiceGrammar.GenerateSrgs(config, culture));

            // Keep the metadata file's dateModified current; create a minimal
            // one on first save so every installed pack has one to read back.
            var meta = LoadMetadata(culture) ?? new VoicePackMetadata { Locale = culture };
            meta.DateModified = DateTimeOffset.UtcNow;
            SaveMetadata(meta, culture);
        }

        /// <summary>
        /// §2.5 autosave tier: a draft is saved on every editor change,
        /// separately from the installed pack, so a browser refresh or crash
        /// mid-edit doesn't lose unsaved translation work. Cleared once the
        /// user actually saves (which writes the real pack) or explicitly
        /// discards it.
        /// </summary>
        public void SaveDraft(VoicePhrasesConfig config, string culture = DefaultCulture)
        {
            Directory.CreateDirectory(LocaleDir(culture));
            File.WriteAllText(DraftPath(culture), JsonSerializer.Serialize(config, _jsonOpts));
        }

        public VoicePhrasesConfig? LoadDraft(string culture = DefaultCulture)
        {
            var path = DraftPath(culture);
            if (!File.Exists(path)) return null;
            try
            {
                return JsonSerializer.Deserialize<VoicePhrasesConfig>(File.ReadAllText(path), _jsonOpts);
            }
            catch
            {
                return null;
            }
        }

        public bool HasDraft(string culture = DefaultCulture) => File.Exists(DraftPath(culture));

        public void ClearDraft(string culture = DefaultCulture)
        {
            var path = DraftPath(culture);
            if (File.Exists(path)) File.Delete(path);
        }

        public VoicePackMetadata? LoadMetadata(string culture = DefaultCulture)
        {
            var path = MetaPath(culture);
            if (!File.Exists(path)) return null;
            try
            {
                return JsonSerializer.Deserialize<VoicePackMetadata>(File.ReadAllText(path), _jsonOpts);
            }
            catch
            {
                return null;
            }
        }

        public void SaveMetadata(VoicePackMetadata meta, string culture = DefaultCulture)
        {
            Directory.CreateDirectory(LocaleDir(culture));
            File.WriteAllText(MetaPath(culture), JsonSerializer.Serialize(meta, _jsonOpts));
        }

        /// <summary>
        /// Copies the currently-installed pack for <paramref name="culture"/>
        /// into Grammars\&lt;culture&gt;\history\&lt;timestamp&gt;-v&lt;version&gt;\
        /// before it gets overwritten, then prunes down to
        /// <see cref="MaxHistorySnapshots"/>. No-ops if nothing is installed
        /// yet (first save for this culture). Best-effort: a snapshot
        /// failure must never block the save the caller actually asked for.
        /// </summary>
        private void SnapshotBeforeOverwrite(string culture)
        {
            var jsonPath = JsonPath(culture);
            if (!File.Exists(jsonPath)) return;

            try
            {
                var version = LoadMetadata(culture)?.Version ?? 1;
                var snapshotId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-v{version}";
                var dir = SnapshotDir(culture, snapshotId);
                Directory.CreateDirectory(dir);

                File.Copy(jsonPath, Path.Combine(dir, Path.GetFileName(jsonPath)), overwrite: true);
                if (File.Exists(SrgsPath(culture)))
                    File.Copy(SrgsPath(culture), Path.Combine(dir, Path.GetFileName(SrgsPath(culture))), overwrite: true);
                if (File.Exists(MetaPath(culture)))
                    File.Copy(MetaPath(culture), Path.Combine(dir, Path.GetFileName(MetaPath(culture))), overwrite: true);

                PruneHistory(culture);
            }
            catch
            {
                // Best-effort — see remarks above.
            }
        }

        private static void PruneHistory(string culture)
        {
            var dir = HistoryDir(culture);
            if (!Directory.Exists(dir)) return;

            // snapshotId is "yyyyMMdd-HHmmss-vN", so ordinal string order is
            // chronological order regardless of the trailing version number.
            var stale = Directory.GetDirectories(dir)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Skip(MaxHistorySnapshots);
            foreach (var old in stale)
                Directory.Delete(old, recursive: true);
        }

        public sealed record HistorySnapshotInfo(string SnapshotId, VoicePackMetadata? Meta);

        /// <summary>Newest-first list of retained snapshots for a culture, for the Settings.cshtml version history list.</summary>
        public IReadOnlyList<HistorySnapshotInfo> ListHistory(string culture)
        {
            var dir = HistoryDir(culture);
            if (!Directory.Exists(dir)) return Array.Empty<HistorySnapshotInfo>();

            return Directory.GetDirectories(dir)
                .Select(Path.GetFileName)
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .OrderByDescending(id => id, StringComparer.OrdinalIgnoreCase)
                .Select(id => new HistorySnapshotInfo(id, LoadHistorySnapshotMetadata(culture, id)))
                .ToList();
        }

        /// <summary>
        /// Loads a snapshot's phrase config for restore. Returns null if the
        /// snapshot doesn't exist or snapshotId isn't a bare folder name
        /// (defends against path traversal — snapshotId arrives from the client).
        /// </summary>
        public VoicePhrasesConfig? LoadHistorySnapshot(string culture, string snapshotId)
        {
            if (!IsSafeSnapshotId(snapshotId)) return null;

            var path = Path.Combine(SnapshotDir(culture, snapshotId), $"Commands.{culture}.json");
            if (!File.Exists(path)) return null;

            try
            {
                return JsonSerializer.Deserialize<VoicePhrasesConfig>(File.ReadAllText(path), _jsonOpts);
            }
            catch
            {
                return null;
            }
        }

        public VoicePackMetadata? LoadHistorySnapshotMetadata(string culture, string snapshotId)
        {
            if (!IsSafeSnapshotId(snapshotId)) return null;

            var path = Path.Combine(SnapshotDir(culture, snapshotId), $"Commands.{culture}.meta.json");
            if (!File.Exists(path)) return null;

            try
            {
                return JsonSerializer.Deserialize<VoicePackMetadata>(File.ReadAllText(path), _jsonOpts);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsSafeSnapshotId(string snapshotId) =>
            !string.IsNullOrWhiteSpace(snapshotId)
            && snapshotId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !snapshotId.Contains("..", StringComparison.Ordinal);

        /// <summary>
        /// One-time move of the pre-v2.4 single voice_phrases.json into
        /// Grammars\en-GB\Commands.en-GB.json. The old file is renamed
        /// (not deleted) so it's still there as a backup if anything about
        /// the migration looks wrong.
        /// </summary>
        private static void MigrateLegacyFile()
        {
            if (!File.Exists(LegacyFilePath)) return;

            try
            {
                Directory.CreateDirectory(LocaleDir(DefaultCulture));
                File.Copy(LegacyFilePath, JsonPath(DefaultCulture), overwrite: false);

                if (!File.Exists(MetaPath(DefaultCulture)))
                {
                    var meta = new VoicePackMetadata
                    {
                        Locale = DefaultCulture,
                        Description = "Migrated from the pre-2.4 single-file voice phrase store.",
                    };
                    Directory.CreateDirectory(LocaleDir(DefaultCulture));
                    File.WriteAllText(MetaPath(DefaultCulture), JsonSerializer.Serialize(meta, _jsonOpts));
                }

                var backupPath = Path.Combine(AppDataRoot, "voice_phrases.legacy.json");
                if (!File.Exists(backupPath))
                    File.Move(LegacyFilePath, backupPath);
            }
            catch
            {
                // Migration is best-effort — if it fails, Load() falls back
                // to BuildDefaults() same as a fresh install would.
            }
        }

        public static VoicePhrasesConfig BuildDefaults() => new()
        {
            Version = 6,
            SimpleCommands = new()
            {
                ["SwapVFO"]          = ["swap v f o", "swap v f os", "swap a and b", "swap a b", "switch v f o", "switch a and b"],
                ["NudgeUp"]          = ["tune up", "step up", "frequency up", "up one step", "tune higher", "nudge up", "nudge higher"],
                ["NudgeDown"]        = ["tune down", "step down", "frequency down", "down one step", "tune lower", "nudge down", "nudge lower"],
                ["BandUp"]           = ["band up", "next band", "up one band"],
                ["BandDown"]         = ["band down", "previous band", "down one band"],
                ["StatusFrequency"]  = ["what frequency", "what is my frequency", "read frequency", "current frequency"],
                ["StatusMode"]       = ["what mode", "what is my mode", "read mode", "current mode"],
                ["StatusBand"]       = ["what band", "what band am I on", "current band"],
                ["TxOn"]             = ["key transmitter", "start transmitting", "transmit now"],
                ["TxOff"]            = ["stop transmitting", "go to receive", "transmit off"],
                ["SplitOn"]          = ["split on", "enable split", "split transmit"],
                ["SplitOff"]         = ["split off", "disable split", "simplex"],
                ["Help"]             = ["help", "command help", "what can I say", "list commands"],
                ["NudgeIfWidthUp"]   = ["filter wider", "wider filter", "increase bandwidth", "i f width up"],
                ["NudgeIfWidthDown"] = ["filter narrower", "narrower filter", "decrease bandwidth", "i f width down"],
            },
            SetMode = new()
            {
                Triggers   = ["mode", "set mode"],
                Vocabulary = new()
                {
                    ["USB"]    = ["u s b", "upper sideband"],
                    ["LSB"]    = ["l s b", "lower sideband"],
                    ["CW-U"]   = ["c w", "see double you"],
                    ["AM"]     = ["a m"],
                    ["FM"]     = ["f m"],
                    ["DATA-U"] = ["data", "digital"],
                    ["DATA-L"] = ["data l"],
                    ["RTTY-L"] = ["r t t y"],
                },
            },
            SetBand = new()
            {
                Triggers   = ["go to", "switch to"],
                // Each band carries its natural spoken form plus a digit-spelled
                // alternate ("two zero metres" etc.). The digit forms exist to
                // break inherently-confusable acoustic pairs: "twenty metres"
                // and "eighty metres" share the same rhythm and "-ty metres"
                // tail, so a quiet/marginal mic (or a strong accent) can smear
                // the only distinguishing syllable and SAPI snaps to the wrong
                // rule at good confidence. "two zero" vs "eight zero" have no
                // such overlap, giving the operator a reliable way to say it.
                Vocabulary = new()
                {
                    ["160"] = ["one sixty metres", "one six zero metres", "top band"],
                    ["80"]  = ["eighty metres",    "eight zero metres"],
                    ["60"]  = ["sixty metres",     "six zero metres"],
                    ["40"]  = ["forty metres",     "four zero metres"],
                    ["30"]  = ["thirty metres",    "three zero metres"],
                    ["20"]  = ["twenty metres",    "two zero metres"],
                    ["17"]  = ["seventeen metres", "one seven metres"],
                    ["15"]  = ["fifteen metres",   "one five metres"],
                    ["12"]  = ["twelve metres",    "one two metres"],
                    ["10"]  = ["ten metres",       "one zero metres"],
                    ["6"]   = ["six metres"],
                    ["4"]   = ["four metres"],
                },
            },
            Macros = new()
            {
                new() { Name = "Copy A to B",    Phrases = ["copy a to b", "a to b"],         Cat = "AB;",   Category = "Macros" },
                new() { Name = "Copy B to A",    Phrases = ["copy b to a", "b to a"],         Cat = "BA;",   Category = "Macros" },
                new() { Name = "Fine step up",   Phrases = ["fine up", "fine step up"],       Cat = "UP;",   Category = "Macros" },
                new() { Name = "Fine step down", Phrases = ["fine down", "fine step down"],   Cat = "DN;",   Category = "Macros" },
                new() { Name = "NR on",           Phrases = ["noise reduction on", "n r on"],           Cat = "NR01;", Category = "Noise Reduction" },
                new() { Name = "NR off",          Phrases = ["noise reduction off", "n r off"],          Cat = "NR00;", Category = "Noise Reduction" },
                new() { Name = "NB on",           Phrases = ["noise blanker on", "n b on"],              Cat = "NB01;", Category = "Noise Reduction" },
                new() { Name = "NB off",          Phrases = ["noise blanker off", "n b off"],            Cat = "NB00;", Category = "Noise Reduction" },
                // Roofing filter — CAT strings are FTdx101 specific; verify and adjust if needed.
                new() { Name = "Roofing 3 kHz",   Phrases = ["roofing three kilohertz", "roofing three k"],   Cat = "RF03;", Category = "Macros" },
                new() { Name = "Roofing 6 kHz",   Phrases = ["roofing six kilohertz", "roofing six k"],       Cat = "RF04;", Category = "Macros" },
                new() { Name = "Roofing 12 kHz",  Phrases = ["roofing twelve kilohertz", "roofing twelve k"], Cat = "RF05;", Category = "Macros" },
            },
            SetAttenuator = new()
            {
                Triggers   = ["attenuator", "set attenuator", "a t t"],
                Vocabulary = new()
                {
                    ["off"] = ["off"],
                    ["6"]   = ["six d b"],
                    ["12"]  = ["twelve d b"],
                    ["18"]  = ["eighteen d b"],
                },
            },
            SetPreamp = new()
            {
                Triggers   = ["preamp", "set preamp", "preamplifier"],
                Vocabulary = new()
                {
                    ["off"] = ["off", "i p o"],
                    ["1"]   = ["one", "amp one"],
                    ["2"]   = ["two", "amp two"],
                },
            },
            SetAgc = new()
            {
                Triggers   = ["a g c", "set a g c"],
                Vocabulary = new()
                {
                    ["off"]  = ["off"],
                    ["fast"] = ["fast"],
                    ["mid"]  = ["medium", "mid"],
                    ["slow"] = ["slow"],
                    ["auto"] = ["auto", "automatic"],
                },
            },
            SetAfGain = new()
            {
                Triggers   = ["a f gain", "audio gain", "set a f gain"],
                Vocabulary = new()
                {
                    ["0"]   = ["zero", "mute", "silent"],
                    ["10"]  = ["ten"],
                    ["20"]  = ["twenty"],
                    ["25"]  = ["twenty five"],
                    ["30"]  = ["thirty"],
                    ["40"]  = ["forty"],
                    ["50"]  = ["fifty"],
                    ["60"]  = ["sixty"],
                    ["70"]  = ["seventy"],
                    ["75"]  = ["seventy five"],
                    ["80"]  = ["eighty"],
                    ["90"]  = ["ninety"],
                    ["100"] = ["one hundred", "maximum", "full"],
                },
            },
            SetNudgeStep = new()
            {
                Triggers   = ["set step", "step size", "nudge step"],
                Vocabulary = new()
                {
                    ["10"]     = ["ten hertz"],
                    ["100"]    = ["one hundred hertz", "hundred hertz"],
                    ["1000"]   = ["one kilohertz", "one k"],
                    ["10000"]  = ["ten kilohertz", "ten k"],
                    ["100000"] = ["one hundred kilohertz", "hundred k"],
                },
            },
            SetFrequency = new()
            {
                Triggers   = ["tune to", "set frequency to", "set the frequency to", "tune tae"],
                Megahertz  = ["megahertz", "mega hertz"],
                Point      = ["point"],
                FracDigits = ["zero", "oh", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"],
                Mhz        = new()
                {
                    ["1"]  = ["one"],   ["2"]  = ["two"],   ["3"]  = ["three"],
                    ["4"]  = ["four"],  ["5"]  = ["five"],  ["6"]  = ["six"],
                    ["7"]  = ["seven"], ["8"]  = ["eight"], ["9"]  = ["nine"],
                    ["10"] = ["ten"],   ["11"] = ["eleven"],["12"] = ["twelve"],
                    ["13"] = ["thirteen"], ["14"] = ["fourteen"], ["15"] = ["fifteen"],
                    ["16"] = ["sixteen"],  ["17"] = ["seventeen"],["18"] = ["eighteen"],
                    ["19"] = ["nineteen"], ["20"] = ["twenty"],
                    ["21"] = ["twenty one"],  ["24"] = ["twenty four"],
                    ["28"] = ["twenty eight"],["29"] = ["twenty nine"],
                    ["30"] = ["thirty"],
                    ["50"] = ["fifty"],  ["51"] = ["fifty one"], ["52"] = ["fifty two"],
                    ["70"] = ["seventy"],["71"] = ["seventy one"],
                },
            },
        };
    }
}
