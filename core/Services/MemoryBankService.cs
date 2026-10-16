using System.Text.Json;
using RadioWebControl.Core.Models;

namespace RadioWebControl.Core.Services
{
    /// <summary>
    /// Named snapshots of the whole memory list, kept in one JSON file beside
    /// memories.json. Radio-agnostic, like <see cref="MemoryService"/>; the
    /// app passes the file path in.
    /// </summary>
    public class MemoryBankService
    {
        private static readonly JsonSerializerOptions _opts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly MemoryService _memoryService;
        private readonly string _path;
        private Dictionary<string, List<AppMemory>> _banks = new();

        /// <param name="path">Full path of memory-banks.json. The app passes its
        /// user-data location; tests pass a scratch file.</param>
        public MemoryBankService(MemoryService memoryService, string path)
        {
            _memoryService = memoryService;
            _path = path;
            LoadFromDisk();
        }

        private void LoadFromDisk()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var json = File.ReadAllText(_path);
                _banks = JsonSerializer.Deserialize<Dictionary<string, List<AppMemory>>>(json, _opts)
                    ?? new Dictionary<string, List<AppMemory>>();
            }
            catch
            {
                _banks = new Dictionary<string, List<AppMemory>>();
            }
        }

        private void SaveToDisk()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_banks, _opts));
        }

        /// <summary>
        /// The bank last loaded into, or saved from, the working list, while
        /// the two still match by name. Not kept across a restart.
        /// </summary>
        public string? LoadedBank { get; private set; }

        /// <summary>The working list was replaced by something that is not a bank.</summary>
        public void ForgetLoadedBank() => LoadedBank = null;

        public IReadOnlyList<string> GetBankNames() =>
            _banks.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        public async Task SaveBankAsync(string name)
        {
            // Copies, not the live objects: a bank is a snapshot, and must
            // not change when the working list is edited afterwards.
            var memories = _memoryService.GetAll().Select(m => m.Clone()).ToList();
            await _lock.WaitAsync();
            try
            {
                _banks[name] = memories;
                SaveToDisk();
                LoadedBank = name;
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Adds one memory to the end of a bank, without loading the bank.
        /// When that bank is the one in the working list (<see cref="LoadedBank"/>)
        /// it goes on the end of the working list too, so a later save of
        /// the working list over the bank does not drop it. Returns false,
        /// changing nothing, when there is no such bank.
        /// </summary>
        public async Task<bool> AddToBankAsync(string name, AppMemory memory)
        {
            bool alsoWorking;
            await _lock.WaitAsync();
            try
            {
                if (!_banks.TryGetValue(name, out var bank)) return false;
                var copy = memory.Clone();
                copy.Id = bank.Count > 0 ? bank.Max(m => m.Id) + 1 : 1;
                copy.SortOrder = bank.Count > 0 ? bank.Max(m => m.SortOrder) + 1 : 1;
                bank.Add(copy);
                SaveToDisk();
                alsoWorking = LoadedBank == name;
            }
            finally { _lock.Release(); }

            if (alsoWorking)
            {
                var working = memory.Clone();
                working.Id = 0;
                var all = _memoryService.GetAll();
                working.SortOrder = all.Count > 0 ? all.Max(m => m.SortOrder) + 1 : 1;
                await _memoryService.AddAsync(working);
            }
            return true;
        }

        /// <summary>
        /// Create a bank from an arbitrary entry list, bypassing the
        /// "snapshot current memories" pattern of <see cref="SaveBankAsync"/>.
        /// Used for the themed-starter-bank flow where each themed bank is
        /// derived from the bundled region JSON, not from whatever the user
        /// currently has loaded. Returns true if the bank was written,
        /// false if it already existed and overwrite was false.
        /// </summary>
        public async Task<bool> CreateBankWithEntriesAsync(string name, List<AppMemory> entries, bool overwriteIfExists)
        {
            await _lock.WaitAsync();
            try
            {
                if (!overwriteIfExists && _banks.ContainsKey(name)) return false;
                _banks[name] = entries;
                SaveToDisk();
                return true;
            }
            finally { _lock.Release(); }
        }

        public async Task<bool> LoadBankAsync(string name)
        {
            await _lock.WaitAsync();
            List<AppMemory>? bank;
            try
            {
                if (!_banks.TryGetValue(name, out bank)) return false;
            }
            finally { _lock.Release(); }

            // Clone entries so bank contents are not mutated by MemoryService.
            // Every field, including the advanced ones (antenna, IF width and
            // shift, roofing, NB, NR, AGC, power, notes) -- listing six of them
            // here is what lost the rest on every bank load before 2026-09-20.
            var copies = bank.Select(m => m.Clone()).ToList();

            await _memoryService.ReplaceAllAsync(copies);
            LoadedBank = name;
            return true;
        }

        public async Task<bool> DeleteBankAsync(string name)
        {
            await _lock.WaitAsync();
            try
            {
                if (!_banks.ContainsKey(name)) return false;
                _banks.Remove(name);
                SaveToDisk();
                if (LoadedBank == name) LoadedBank = null;
                return true;
            }
            finally { _lock.Release(); }
        }
    }
}
