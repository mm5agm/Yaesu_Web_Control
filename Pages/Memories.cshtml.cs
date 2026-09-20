using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Pages
{
    public class MemoriesModel : PageModel
    {
        private readonly MemoryService _memoryService;

        [TempData]
        public string? StatusMessage { get; set; }

        [BindProperty]
        public List<AppMemory> Memories { get; set; } = new();

        public MemoriesModel(MemoryService memoryService)
        {
            _memoryService = memoryService;
        }

        public IActionResult OnGet()
        {
            Memories = _memoryService.GetAll().ToList();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            // Remove empty rows (no label and no frequency), then take the
            // order the operator left the rows in. The form binds by index,
            // so a row moved or sorted on the page still arrives at its
            // original index; the page writes its new position into the
            // hidden SortOrder field, and that is what we sort on here.
            // OrderBy is stable, so rows with equal SortOrder (an untouched
            // page, or an old memories.json with none) keep their index order.
            Memories = Memories
                .Where(m => m.FrequencyHz > 0 || !string.IsNullOrWhiteSpace(m.Label))
                .OrderBy(m => m.SortOrder)
                .ToList();

            // Assign sort order from list position
            for (int i = 0; i < Memories.Count; i++)
                Memories[i].SortOrder = i + 1;

            await _memoryService.ReplaceAllAsync(Memories);
            StatusMessage = $"✓ {Memories.Count} memories saved.";
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostDeleteAsync(int id)
        {
            await _memoryService.DeleteAsync(id);
            StatusMessage = "✓ Memory deleted.";
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostAddAsync()
        {
            await _memoryService.AddAsync(new AppMemory());
            return RedirectToPage();
        }
    }
}
