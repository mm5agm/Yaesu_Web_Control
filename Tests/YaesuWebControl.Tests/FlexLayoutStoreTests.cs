using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaesu_Web_Control.Models;
using Yaesu_Web_Control.Services.Flex;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// FlexLayoutStore — named Flex UI arrangements persisted to a separate
    /// flex-layouts.json. The payload is opaque; these tests pin the metadata
    /// rules, the atomic/round-trip behaviour, and the promise that a newer
    /// store version never causes a user's layouts to be discarded.
    ///
    /// The store takes a scratch path here, never the real AppData file.
    /// </summary>
    public class FlexLayoutStoreTests : IDisposable
    {
        private readonly string _dir =
            Path.Combine(Path.GetTempPath(), "ywc-flex-tests-" + Guid.NewGuid().ToString("N"));

        private string FilePath => Path.Combine(_dir, "flex-layouts.json");

        private FlexLayoutStore NewStore() =>
            new(NullLogger<FlexLayoutStore>.Instance, FilePath);

        private static JsonNode Layout(string mode = "row") =>
            JsonNode.Parse(
                "{\"global\":{\"tabEnableClose\":true},\"layout\":{\"type\":\"" + mode + "\"}}")!;

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public async Task CreateThenGetAndListRoundTrips()
        {
            var store = NewStore();
            var result = await store.CreateAsync("abc123", "Desk", Layout());

            Assert.Equal(FlexLayoutMutationStatus.Ok, result.Status);
            Assert.Equal("abc123", result.Summary!.Id);
            Assert.Equal("Desk", result.Summary.Name);

            var detail = store.Get("abc123");
            Assert.NotNull(detail);
            Assert.Equal("row", detail!.Layout!["layout"]!["type"]!.GetValue<string>());

            var snapshot = store.GetSnapshot();
            Assert.Single(snapshot.Layouts);
            Assert.Equal(FlexLayoutDefaults.DefaultId, snapshot.ActiveId);
        }

        [Fact]
        public async Task CreateRejectsDuplicateId()
        {
            var store = NewStore();
            await store.CreateAsync("abc123", "Desk", Layout());

            var again = await store.CreateAsync("abc123", "Other", Layout());
            Assert.Equal(FlexLayoutMutationStatus.Conflict, again.Status);
        }

        [Theory]
        [InlineData("", "Name", "id")]
        [InlineData("bad id!", "Name", "id")]
        [InlineData("__default__", "Name", "id")]
        [InlineData("okid", "", "name")]
        [InlineData("okid", "01234567890123456789012345678901234567890123456789", "name")]
        public async Task CreateRejectsInvalidMetadata(string id, string name, string _)
        {
            var store = NewStore();
            var result = await store.CreateAsync(id, name, Layout());
            Assert.Equal(FlexLayoutMutationStatus.Invalid, result.Status);
        }

        [Fact]
        public async Task CreateRejectsNonObjectPayload()
        {
            var store = NewStore();
            var result = await store.CreateAsync("abc123", "Desk", JsonNode.Parse("[1,2,3]"));
            Assert.Equal(FlexLayoutMutationStatus.Invalid, result.Status);
        }

        [Fact]
        public async Task UpdateRenamesAndReplacesPayload()
        {
            var store = NewStore();
            await store.CreateAsync("abc123", "Desk", Layout());

            var result = await store.UpdateAsync("abc123", "Shack", Layout("tabset"));
            Assert.Equal(FlexLayoutMutationStatus.Ok, result.Status);
            Assert.Equal("Shack", result.Summary!.Name);

            var detail = store.Get("abc123");
            Assert.Equal("Shack", detail!.Name);
            Assert.Equal("tabset", detail.Layout!["layout"]!["type"]!.GetValue<string>());
        }

        [Fact]
        public async Task UpdateWithNullLayoutKeepsPayload()
        {
            var store = NewStore();
            await store.CreateAsync("abc123", "Desk", Layout());

            var result = await store.UpdateAsync("abc123", "Renamed", null);
            Assert.Equal(FlexLayoutMutationStatus.Ok, result.Status);

            var detail = store.Get("abc123");
            Assert.NotNull(detail!.Layout);
            Assert.Equal("row", detail.Layout!["layout"]!["type"]!.GetValue<string>());
        }

        [Fact]
        public async Task UpdateHonoursOptimisticConcurrencyToken()
        {
            var store = NewStore();
            var created = await store.CreateAsync("abc123", "Desk", Layout());

            var stale = await store.UpdateAsync(
                "abc123", "Desk", Layout("tabset"), created.Summary!.UpdatedAt.AddSeconds(-5));
            Assert.Equal(FlexLayoutMutationStatus.Conflict, stale.Status);
        }

        [Fact]
        public async Task UpdateAndDeleteOnDefaultAreProtected()
        {
            var store = NewStore();
            Assert.Equal(FlexLayoutMutationStatus.Protected,
                (await store.UpdateAsync(FlexLayoutDefaults.DefaultId, "x", Layout())).Status);
            Assert.Equal(FlexLayoutMutationStatus.Protected,
                (await store.DeleteAsync(FlexLayoutDefaults.DefaultId)).Status);
        }

        [Fact]
        public async Task DeleteFallsActiveBackToDefault()
        {
            var store = NewStore();
            await store.CreateAsync("abc123", "Desk", Layout());
            await store.SetActiveAsync("abc123");
            Assert.Equal("abc123", store.GetSnapshot().ActiveId);

            var result = await store.DeleteAsync("abc123");
            Assert.Equal(FlexLayoutMutationStatus.Ok, result.Status);
            Assert.Empty(store.GetSnapshot().Layouts);
            Assert.Equal(FlexLayoutDefaults.DefaultId, store.GetSnapshot().ActiveId);
        }

        [Fact]
        public async Task SetActiveRejectsUnknownId()
        {
            var store = NewStore();
            Assert.Equal(FlexLayoutMutationStatus.NotFound,
                (await store.SetActiveAsync("nope")).Status);
            Assert.Equal(FlexLayoutMutationStatus.Ok,
                (await store.SetActiveAsync(FlexLayoutDefaults.DefaultId)).Status);
        }

        [Fact]
        public async Task CapIsEnforced()
        {
            var store = NewStore();
            for (int i = 0; i < FlexLayoutStore.MaxLayouts; i++)
            {
                var r = await store.CreateAsync($"id{i}", $"L{i}", Layout());
                Assert.Equal(FlexLayoutMutationStatus.Ok, r.Status);
            }

            var over = await store.CreateAsync("one-too-many", "Over", Layout());
            Assert.Equal(FlexLayoutMutationStatus.CapReached, over.Status);
        }

        [Fact]
        public async Task PersistsAcrossInstances()
        {
            var first = NewStore();
            await first.CreateAsync("abc123", "Desk", Layout());
            await first.SetActiveAsync("abc123");

            var second = new FlexLayoutStore(NullLogger<FlexLayoutStore>.Instance, FilePath);
            Assert.Equal("abc123", second.GetSnapshot().ActiveId);
            Assert.Equal("Desk", second.Get("abc123")!.Name);
            Assert.Equal("row", second.Get("abc123")!.Layout!["layout"]!["type"]!.GetValue<string>());
        }

        [Fact]
        public async Task WriteIsAtomicAndLeavesNoTempFile()
        {
            var store = NewStore();
            await store.CreateAsync("abc123", "Desk", Layout());
            Assert.False(File.Exists(FilePath + ".tmp"));
            Assert.True(File.Exists(FilePath));
        }

        [Fact]
        public async Task NewerStoreVersionDoesNotDiscardLayouts()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(FilePath,
                """
                {
                  "storeVersion": 99,
                  "activeId": "abc123",
                  "layouts": [
                    { "id": "abc123", "name": "Desk",
                      "layout": { "layout": { "type": "row" } },
                      "createdAt": "2026-01-01T00:00:00+00:00",
                      "updatedAt": "2026-01-01T00:00:00+00:00" }
                  ]
                }
                """);

            var store = NewStore();
            Assert.Single(store.GetSnapshot().Layouts);
            Assert.Equal("Desk", store.Get("abc123")!.Name);
            Assert.Equal("abc123", store.GetSnapshot().ActiveId);

            // A subsequent write keeps the record and stamps the current version.
            await store.CreateAsync("second", "Other", Layout());
            var text = File.ReadAllText(FilePath);
            Assert.Contains("\"storeVersion\": 1", text);
            Assert.Contains("abc123", text);
            Assert.Contains("second", text);
        }

        [Fact]
        public void CorruptFileStartsEmptyWithoutThrowing()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(FilePath, "{ this is not json");
            var store = NewStore();
            Assert.Empty(store.GetSnapshot().Layouts);
            Assert.Equal(FlexLayoutDefaults.DefaultId, store.GetSnapshot().ActiveId);
        }

        [Fact]
        public async Task ActiveFallsBackWhenReferencedLayoutIsMissing()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(FilePath,
                """{ "storeVersion": 1, "activeId": "ghost", "layouts": [] }""");

            var store = NewStore();
            Assert.Equal(FlexLayoutDefaults.DefaultId, store.GetSnapshot().ActiveId);
        }
    }
}
