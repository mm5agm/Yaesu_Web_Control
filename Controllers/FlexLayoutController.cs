using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Models;
using Yaesu_Web_Control.Services.Flex;

namespace Yaesu_Web_Control.Controllers
{
    /// <summary>
    /// Named Flex UI workspace arrangements. The layout payload is treated as
    /// opaque JSON — see <see cref="FlexLayoutStore"/> and Models/FlexLayout.cs
    /// for why the server does not model tabs or components.
    /// </summary>
    [ApiController]
    [Route("api/flexlayouts")]
    public class FlexLayoutController : ControllerBase
    {
        private readonly IFlexLayoutStore _store;

        public FlexLayoutController(IFlexLayoutStore store)
        {
            _store = store;
        }

        [HttpGet]
        public IActionResult List() => Ok(_store.GetSnapshot());

        [HttpGet("{id}")]
        public IActionResult Get(string id)
        {
            var layout = _store.Get(id);
            return layout is null
                ? NotFound(new { error = "Layout not found." })
                : Ok(layout);
        }

        public sealed class CreateRequest
        {
            public string? Id { get; set; }
            public string? Name { get; set; }
            public JsonNode? Layout { get; set; }
            public bool? SetActive { get; set; }
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateRequest request, CancellationToken ct)
        {
            var id = string.IsNullOrWhiteSpace(request?.Id)
                ? Guid.NewGuid().ToString("N")
                : request!.Id!;
            var result = await _store.CreateAsync(id, request?.Name ?? "", request?.Layout, ct);
            if (result.Status != FlexLayoutMutationStatus.Ok) return MapError(result);

            if (request?.SetActive == true) await _store.SetActiveAsync(id, ct);
            return Ok(new { layout = result.Summary, activeId = _store.GetSnapshot().ActiveId });
        }

        public sealed class UpdateRequest
        {
            public string? Name { get; set; }
            public JsonNode? Layout { get; set; }
            public DateTimeOffset? UpdatedAt { get; set; }
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(string id, [FromBody] UpdateRequest request, CancellationToken ct)
        {
            var result = await _store.UpdateAsync(id, request?.Name, request?.Layout, request?.UpdatedAt, ct);
            return result.Status != FlexLayoutMutationStatus.Ok
                ? MapError(result)
                : Ok(new { layout = result.Summary });
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id, CancellationToken ct)
        {
            var result = await _store.DeleteAsync(id, ct);
            return result.Status != FlexLayoutMutationStatus.Ok
                ? MapError(result)
                : Ok(new { deleted = id, activeId = _store.GetSnapshot().ActiveId });
        }

        public sealed class ActiveRequest
        {
            public string? Id { get; set; }
        }

        [HttpPut("active")]
        public async Task<IActionResult> SetActive([FromBody] ActiveRequest request, CancellationToken ct)
        {
            var result = await _store.SetActiveAsync(request?.Id ?? "", ct);
            return result.Status != FlexLayoutMutationStatus.Ok
                ? MapError(result)
                : Ok(new { activeId = _store.GetSnapshot().ActiveId });
        }

        private IActionResult MapError(FlexLayoutMutation result) => result.Status switch
        {
            FlexLayoutMutationStatus.NotFound => NotFound(new { error = result.Error }),
            FlexLayoutMutationStatus.Conflict => Conflict(new { error = result.Error }),
            FlexLayoutMutationStatus.CapReached =>
                StatusCode(StatusCodes.Status422UnprocessableEntity, new { error = result.Error }),
            FlexLayoutMutationStatus.Protected => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }
}
