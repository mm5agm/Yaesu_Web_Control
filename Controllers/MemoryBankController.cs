using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;
using RadioWebControl.Core.Models;
using RadioWebControl.Core.Services;

namespace Yaesu_Web_Control.Controllers
{
    [ApiController]
    [Route("api/memorybank")]
    public class MemoryBankController : ControllerBase
    {
        private readonly MemoryBankService _bankService;

        public MemoryBankController(MemoryBankService bankService)
        {
            _bankService = bankService;
        }

        [HttpGet]
        public IActionResult GetBankNames() => Ok(_bankService.GetBankNames());

        public class SaveBankRequest { public string Name { get; set; } = ""; }

        [HttpPost("save")]
        public async Task<IActionResult> SaveBank([FromBody] SaveBankRequest request)
        {
            var name = request.Name?.Trim() ?? "";
            if (string.IsNullOrEmpty(name))
                return BadRequest(new { error = "Bank name is required" });
            await _bankService.SaveBankAsync(name);
            return Ok(new { name });
        }

        [HttpPost("{name}/load")]
        public async Task<IActionResult> LoadBank(string name)
        {
            if (!await _bankService.LoadBankAsync(name))
                return NotFound();
            return Ok();
        }

        [HttpDelete("{name}")]
        public async Task<IActionResult> DeleteBank(string name)
        {
            if (!await _bankService.DeleteBankAsync(name))
                return NotFound();
            return Ok();
        }
    }
}
