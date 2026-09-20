using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Yaesu_Web_Control.Services;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// Two silent faults in the memory store, both found while adding row
    /// ordering to the Memories editor on 2026-09-20.
    ///
    /// Loading a memory bank copied six fields and dropped the other ten, so
    /// the receiver setup captured by "Save to Mem" (antenna, IF width, AGC,
    /// power...) vanished on every bank load with nothing to say so. And
    /// every Save renumbered ids from 1, so the Mem panel -- which recalls and
    /// deletes by id -- pointed at the wrong memory after a delete or a
    /// reorder until it happened to reload.
    ///
    /// Both services take a scratch path here; the app's own constructors use
    /// the real files under AppData, which these tests must never touch.
    /// </summary>
    public class MemoryOrderAndBankTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ywc-tests-" + Guid.NewGuid().ToString("N"));

        private MemoryService NewMemoryService() =>
            new MemoryService(Path.Combine(_dir, "memories.json"));

        private MemoryBankService NewBankService(MemoryService memories) =>
            new MemoryBankService(memories, Path.Combine(_dir, "memory-banks.json"));

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static AppMemory FullMemory(string label, long hz) => new AppMemory
        {
            Label = label, FrequencyHz = hz, Mode = "DATA-U",
            ClarifierOffsetHz = 120, RxClarOn = true, TxClarOn = false,
            Antenna = "2", IfWidthCode = "8", IfShiftHz = -40, RoofingCode = "7",
            NbOn = true, NbLevel = 5, NrLevel = "1", AgcMode = "4", PowerWatts = 50,
            Notes = "the whole setup"
        };

        [Fact]
        public async Task BankLoadKeepsEveryField()
        {
            var memories = NewMemoryService();
            await memories.ReplaceAllAsync(new List<AppMemory> { FullMemory("20m FT8", 14_074_000) });
            var banks = NewBankService(memories);
            await banks.SaveBankAsync("Daily");

            // Wipe the working list, then bring the bank back.
            await memories.ReplaceAllAsync(new List<AppMemory>());
            Assert.True(await banks.LoadBankAsync("Daily"));

            var m = Assert.Single(memories.GetAll());
            var expected = FullMemory("20m FT8", 14_074_000);
            foreach (var prop in typeof(AppMemory).GetProperties().Where(p => p.Name is not ("Id" or "SortOrder")))
            {
                Assert.True(Equals(prop.GetValue(expected), prop.GetValue(m)),
                    $"{prop.Name} was lost on bank load: expected {prop.GetValue(expected)}, got {prop.GetValue(m)}");
            }
        }

        [Fact]
        public async Task BankIsASnapshotNotALiveReference()
        {
            var memories = NewMemoryService();
            await memories.ReplaceAllAsync(new List<AppMemory> { FullMemory("before", 7_000_000) });
            var banks = NewBankService(memories);
            await banks.SaveBankAsync("Snap");

            // Edit the working list after the bank was saved...
            var live = memories.GetAll()[0];
            live.Label = "after";
            live.PowerWatts = 5;

            // ...and the bank must still hold what was saved.
            await memories.ReplaceAllAsync(new List<AppMemory>());
            await banks.LoadBankAsync("Snap");
            var m = Assert.Single(memories.GetAll());
            Assert.Equal("before", m.Label);
            Assert.Equal(50, m.PowerWatts);
        }

        [Fact]
        public async Task SaveKeepsIdsWhenRowsAreReorderedOrDeleted()
        {
            var memories = NewMemoryService();
            await memories.ReplaceAllAsync(new List<AppMemory>
            {
                new() { Label = "a", FrequencyHz = 1 },
                new() { Label = "b", FrequencyHz = 2 },
                new() { Label = "c", FrequencyHz = 3 },
            });
            var ids = memories.GetAll().ToDictionary(m => m.Label, m => m.Id);

            // What the editor posts after "b" is deleted and "c" is moved to the top.
            var posted = new List<AppMemory>
            {
                new() { Id = ids["c"], Label = "c", FrequencyHz = 3 },
                new() { Id = ids["a"], Label = "a", FrequencyHz = 1 },
            };
            await memories.ReplaceAllAsync(posted);

            var after = memories.GetAll();
            Assert.Equal(new[] { "c", "a" }, after.Select(m => m.Label));
            Assert.Equal(ids["c"], after[0].Id);
            Assert.Equal(ids["a"], after[1].Id);
            Assert.Equal(new[] { 1, 2 }, after.Select(m => m.SortOrder));
            Assert.Same(after[0], memories.GetById(ids["c"]));
        }

        [Fact]
        public async Task SaveGivesFreshIdsToNewAndDuplicateRows()
        {
            var memories = NewMemoryService();
            await memories.ReplaceAllAsync(new List<AppMemory>
            {
                new() { Id = 7, Label = "kept", FrequencyHz = 1 },
                new() { Id = 0, Label = "new", FrequencyHz = 2 },
                new() { Id = 7, Label = "dup", FrequencyHz = 3 },
            });

            var after = memories.GetAll();
            Assert.Equal(3, after.Select(m => m.Id).Distinct().Count());
            Assert.Equal(7, after.Single(m => m.Label == "kept").Id);
            Assert.True(after.Single(m => m.Label == "new").Id > 7);
            Assert.True(after.Single(m => m.Label == "dup").Id > 7);

            // And the next Add carries on above all of them.
            var added = await memories.AddAsync(new AppMemory { Label = "later", FrequencyHz = 4 });
            Assert.Equal(after.Max(m => m.Id) + 1, added.Id);
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
            var page = new Yaesu_Web_Control.Pages.MemoriesModel(memories)
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
            var page = new Yaesu_Web_Control.Pages.MemoriesModel(memories)
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

        [Fact]
        public async Task OrderSurvivesAReload()
        {
            var path = Path.Combine(_dir, "memories.json");
            var memories = new MemoryService(path);
            await memories.ReplaceAllAsync(new List<AppMemory>
            {
                new() { Label = "second", FrequencyHz = 2 },
                new() { Label = "first", FrequencyHz = 1 },
            });

            var reloaded = new MemoryService(path);
            Assert.Equal(new[] { "second", "first" }, reloaded.GetAll().Select(m => m.Label));
        }
    }
}
