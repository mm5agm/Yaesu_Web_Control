using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RadioWebControl.Core.Models;
using RadioWebControl.Core.Services;
using Xunit;
using Yaesu_Web_Control.Pages;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// The Memories editor's Save handler. The store itself (stable ids, bank
    /// snapshots, order surviving a reload) is tested in core, where it lives;
    /// this is the page's half of the row-ordering work from 2026-09-20.
    ///
    /// The service takes a scratch path here, never the real memories.json
    /// under AppData.
    /// </summary>
    public class MemoriesPageTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ywc-tests-" + Guid.NewGuid().ToString("N"));

        private MemoryService NewMemoryService() =>
            new MemoryService(Path.Combine(_dir, "memories.json"));

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        /// <summary>
        /// The editor's form binds rows by their Memories[i] index, which is
        /// fixed at render time, so a row moved on the page still arrives at
        /// its original index. The page's script writes the new position into
        /// the hidden SortOrder field; this is the server half honouring it.
        /// </summary>
        [Fact]
        public async Task EditorSaveOrdersRowsBySortOrderNotByIndex()
        {
            var memories = NewMemoryService();
            var page = new MemoriesModel(memories)
            {
                Memories = new List<AppMemory>
                {
                    new() { Id = 1, Label = "was first",  FrequencyHz = 1, SortOrder = 3 },
                    new() { Id = 2, Label = "was second", FrequencyHz = 2, SortOrder = 1 },
                    new() { Id = 3, Label = "",           FrequencyHz = 0, SortOrder = 0 }, // empty row, dropped
                    new() { Id = 4, Label = "was fourth", FrequencyHz = 4, SortOrder = 2 },
                }
            };

            await page.OnPostAsync();

            var after = memories.GetAll();
            Assert.Equal(new[] { "was second", "was fourth", "was first" }, after.Select(m => m.Label));
            Assert.Equal(new[] { 2, 4, 1 }, after.Select(m => m.Id));
            Assert.Equal(new[] { 1, 2, 3 }, after.Select(m => m.SortOrder));
        }

        [Fact]
        public async Task EditorSaveKeepsIndexOrderWhenNothingWasMoved()
        {
            // An old memories.json can have SortOrder 0 on every row; an
            // untouched page posts 1..n. Either way the rows must not shuffle.
            var memories = NewMemoryService();
            var page = new MemoriesModel(memories)
            {
                Memories = new List<AppMemory>
                {
                    new() { Id = 1, Label = "a", FrequencyHz = 1, SortOrder = 0 },
                    new() { Id = 2, Label = "b", FrequencyHz = 2, SortOrder = 0 },
                    new() { Id = 3, Label = "c", FrequencyHz = 3, SortOrder = 0 },
                }
            };

            await page.OnPostAsync();

            Assert.Equal(new[] { "a", "b", "c" }, memories.GetAll().Select(m => m.Label));
        }
    }
}
