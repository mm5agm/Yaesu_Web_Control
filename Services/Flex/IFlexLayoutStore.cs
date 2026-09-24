using System.Text.Json.Nodes;
using Yaesu_Web_Control.Models;

namespace Yaesu_Web_Control.Services.Flex
{
    /// <summary>
    /// Contract for storing named Flex UI workspace arrangements.
    /// Implementations are registered as singletons and are thread-safe.
    ///
    /// The stored layout payload is opaque: the store never parses it. It only
    /// enforces metadata rules (id format, name length, payload size, count cap)
    /// so a malformed client cannot grow the file without bound or break reads.
    /// </summary>
    public interface IFlexLayoutStore
    {
        /// <summary>Returns the active preset id plus every stored preset's metadata.</summary>
        FlexLayoutSnapshot GetSnapshot();

        /// <summary>Returns one preset including its layout payload, or null if absent.</summary>
        FlexLayoutDetail? Get(string id);

        /// <summary>
        /// Creates a preset under a client-supplied <paramref name="id"/> (so an
        /// arrangement created offline keeps its identity when the queue flushes).
        /// </summary>
        Task<FlexLayoutMutation> CreateAsync(string id, string name, JsonNode? layout, CancellationToken ct = default);

        /// <summary>
        /// Updates name and/or payload. A null <paramref name="layout"/> leaves the
        /// payload untouched. A null <paramref name="expectedUpdatedAt"/> means
        /// last-write-wins; otherwise a stale token yields <see cref="FlexLayoutMutationStatus.Conflict"/>.
        /// </summary>
        Task<FlexLayoutMutation> UpdateAsync(string id, string? name, JsonNode? layout, DateTimeOffset? expectedUpdatedAt = null, CancellationToken ct = default);

        /// <summary>Deletes a preset. If it was active, active falls back to the default.</summary>
        Task<FlexLayoutMutation> DeleteAsync(string id, CancellationToken ct = default);

        /// <summary>Marks a preset (or the built-in default) as the active arrangement.</summary>
        Task<FlexLayoutMutation> SetActiveAsync(string id, CancellationToken ct = default);
    }
}
