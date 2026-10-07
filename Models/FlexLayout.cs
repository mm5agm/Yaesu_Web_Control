using System.Text.Json.Nodes;

namespace Yaesu_Web_Control.Models
{
    /// <summary>
    /// Named Flex UI workspace arrangements.
    ///
    /// The arrangement itself (<see cref="FlexLayoutDetail.Layout"/>) is an
    /// opaque FlexLayout model. The server never interprets its tabs, ids or
    /// components — it stores and returns the JSON untouched. All validation
    /// and normalisation of a layout happens in the browser
    /// (<c>filterLayoutJson</c> in flex-workspace.js), which is the only place
    /// that knows the current panel set. Keeping the C# side schema-free is
    /// deliberate: it avoids a second source of truth that would drift as
    /// panels are added, renamed or removed.
    /// </summary>
    public static class FlexLayoutDefaults
    {
        /// <summary>Sentinel id for the built-in, read-only default arrangement.</summary>
        public const string DefaultId = "__default__";
    }

    /// <summary>List-view projection — no layout payload.</summary>
    public class FlexLayoutSummary
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
    }

    /// <summary>Full projection including the opaque layout payload.</summary>
    public sealed class FlexLayoutDetail : FlexLayoutSummary
    {
        public JsonNode? Layout { get; init; }
    }

    public sealed class FlexLayoutSnapshot
    {
        public string ActiveId { get; init; } = FlexLayoutDefaults.DefaultId;
        public IReadOnlyList<FlexLayoutSummary> Layouts { get; init; } = Array.Empty<FlexLayoutSummary>();
    }

    public enum FlexLayoutMutationStatus
    {
        Ok,
        NotFound,
        Conflict,
        CapReached,
        Invalid,
        Protected,
    }

    public sealed record FlexLayoutMutation(
        FlexLayoutMutationStatus Status,
        FlexLayoutSummary? Summary = null,
        string? Error = null);
}
