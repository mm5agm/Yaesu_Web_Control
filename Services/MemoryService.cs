using System.Text.Json;

namespace Yaesu_Web_Control.Services
{
    public class MemoryService
    {
        public static readonly string MemoriesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Yaesu Web Control", "memories.json");

        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly string _path;
        private List<AppMemory> _memories = new();
        private int _nextId = 1;

        public MemoryService() : this(MemoriesPath) { }

        /// <summary>Tests point this at a scratch file; the app uses <see cref="MemoriesPath"/>.</summary>
        internal MemoryService(string path)
        {
            _path = path;
            LoadFromDisk();
        }

        /// <summary>Re-read memories.json from disk. Used after an external
        /// import (e.g. the unified backup/restore) has overwritten the file.</summary>
        public void ReloadFromDisk() => LoadFromDisk();

        private void LoadFromDisk()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    _memories = new List<AppMemory>();
                    return;
                }
                var json = File.ReadAllText(_path);
                _memories = JsonSerializer.Deserialize<List<AppMemory>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new List<AppMemory>();
                _nextId = _memories.Count > 0 ? _memories.Max(m => m.Id) + 1 : 1;
            }
            catch
            {
                _memories = new List<AppMemory>();
                _nextId = 1;
            }
        }

        private void SaveToDisk()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path,
                JsonSerializer.Serialize(_memories, new JsonSerializerOptions { WriteIndented = true }));
        }

        public IReadOnlyList<AppMemory> GetAll() =>
            _memories.OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToList();

        public AppMemory? GetById(int id) => _memories.FirstOrDefault(m => m.Id == id);

        public async Task<AppMemory> AddAsync(AppMemory memory)
        {
            await _lock.WaitAsync();
            try
            {
                memory.Id = _nextId++;
                if (memory.SortOrder == 0) memory.SortOrder = _memories.Count + 1;
                _memories.Add(memory);
                SaveToDisk();
                return memory;
            }
            finally { _lock.Release(); }
        }

        public async Task<bool> UpdateAsync(AppMemory memory)
        {
            await _lock.WaitAsync();
            try
            {
                var idx = _memories.FindIndex(m => m.Id == memory.Id);
                if (idx < 0) return false;
                _memories[idx] = memory;
                SaveToDisk();
                return true;
            }
            finally { _lock.Release(); }
        }

        public async Task<bool> DeleteAsync(int id)
        {
            await _lock.WaitAsync();
            try
            {
                var existing = _memories.FirstOrDefault(m => m.Id == id);
                if (existing == null) return false;
                _memories.Remove(existing);
                SaveToDisk();
                return true;
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Replace the whole list, in the order given. Ids are kept where the
        /// caller supplied them: the Mem panel recalls and deletes by id, and
        /// renumbering on every Save (which this did until 2026-09-20) meant
        /// that deleting or reordering a row in the editor silently pointed
        /// every tile below it at a different memory until the panel reloaded.
        /// An id of 0, or one that repeats, gets a fresh number.
        /// </summary>
        public async Task ReplaceAllAsync(List<AppMemory> memories)
        {
            await _lock.WaitAsync();
            try
            {
                var used = new HashSet<int>();
                int next = memories.Where(m => m.Id > 0).Select(m => m.Id).DefaultIfEmpty(0).Max() + 1;
                for (int i = 0; i < memories.Count; i++)
                {
                    if (memories[i].Id <= 0 || !used.Add(memories[i].Id))
                    {
                        memories[i].Id = next++;
                        used.Add(memories[i].Id);
                    }
                    memories[i].SortOrder = i + 1;
                }
                _memories = memories;
                _nextId = next;
                SaveToDisk();
            }
            finally { _lock.Release(); }
        }

        public async Task MergeAsync(List<AppMemory> imported)
        {
            await _lock.WaitAsync();
            try
            {
                foreach (var m in imported)
                {
                    m.Id = _nextId++;
                    m.SortOrder = _memories.Count + 1;
                    _memories.Add(m);
                }
                SaveToDisk();
            }
            finally { _lock.Release(); }
        }
    }
}
