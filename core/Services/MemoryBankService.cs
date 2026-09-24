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
            }
            finally { _lock.Release(); }
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
                return true;
            }
            finally { _lock.Release(); }
        }
    }
}
