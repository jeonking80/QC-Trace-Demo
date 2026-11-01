using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;
using QcTrace.Web.Services;

namespace QcTrace.Web.Pages.Admin;

/// <summary>기준정보: 품목 목록·추가·사용 중지, 품목·검사 항목 CSV 내려받기·올리기</summary>
public class ItemsModel(QcTraceDbContext db, MasterData masterData) : QcPageModel
{
    public sealed record Row(Item Item, int InspectionItemCount, int LotCount);
    public List<Row> Rows { get; private set; } = [];
    public List<string> ImportErrors { get; private set; } = [];

    [BindProperty] public string? NewCode { get; set; }
    [BindProperty] public string? NewName { get; set; }
    [BindProperty] public string? NewSpec { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAddAsync()
    {
        var errors = MasterData.ValidateItem(NewCode, NewName, NewSpec).ToList();
        if (errors.Count == 0 && await db.Items.AnyAsync(i => i.ItemCode == NewCode!.Trim()))
            errors.Add($"품목 코드 {NewCode!.Trim()}가 이미 있습니다.");
        if (errors.Count > 0)
        {
            errors.ForEach(e => ModelState.AddModelError("", e));
            await LoadAsync();
            return Page();
        }
        var item = new Item { ItemCode = NewCode!.Trim(), Name = NewName!.Trim(), Spec = string.IsNullOrWhiteSpace(NewSpec) ? null : NewSpec.Trim() };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        StatusMessage = $"품목 {item.ItemCode}을 추가했습니다. 검사 항목을 넣으세요.";
        return RedirectToPage("./Item", new { id = item.Id });
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var item = await db.Items.FindAsync(id);
        if (item is null) return NotFound();
        item.IsActive = !item.IsActive;
        await db.SaveChangesAsync();
        StatusMessage = item.IsActive ? $"{item.ItemCode}을 다시 사용합니다." : $"{item.ItemCode}을 사용 중지했습니다. 새 LOT 등록에서 빠집니다.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnGetExportAsync(string kind)
    {
        var stamp = Kst.FromUtc(DateTime.UtcNow).ToString("yyyyMMdd");
        return kind == "inspection-items"
            ? File(await masterData.ExportInspectionItemsAsync(), "text/csv; charset=utf-8", $"inspection-items-{stamp}.csv")
            : File(await masterData.ExportItemsAsync(), "text/csv; charset=utf-8", $"items-{stamp}.csv");
    }

    public async Task<IActionResult> OnPostImportAsync(string kind, IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            ModelState.AddModelError("", "CSV 파일을 고르세요.");
        }
        else if (file.Length > MasterData.MaxUploadBytes)
        {
            ModelState.AddModelError("", "CSV 파일은 1MB 이하여야 합니다.");
        }
        else
        {
            string text;
            using (var reader = new StreamReader(file.OpenReadStream(), new UTF8Encoding(false, throwOnInvalidBytes: true)))
            {
                try { text = await reader.ReadToEndAsync(); }
                catch (DecoderFallbackException)
                {
                    ModelState.AddModelError("", "CSV는 UTF-8로 저장해 주세요. (엑셀: CSV UTF-8 형식으로 저장)");
                    await LoadAsync();
                    return Page();
                }
            }
            var result = kind == "inspection-items"
                ? await masterData.ImportInspectionItemsAsync(text)
                : await masterData.ImportItemsAsync(text);
            if (result.Ok)
            {
                StatusMessage = $"{(kind == "inspection-items" ? "검사 항목" : "품목")} CSV 반영: 추가 {result.Added}건, 수정 {result.Updated}건";
                return RedirectToPage();
            }
            ModelState.AddModelError("", $"CSV에 오류가 {result.Errors.Count}건 있어 아무것도 저장하지 않았습니다.");
            ImportErrors = result.Errors;
        }
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Rows = await db.Items.AsNoTracking().OrderBy(i => i.ItemCode)
            .Select(i => new Row(i, i.InspectionItems.Count, db.Lots.Count(l => l.ItemId == i.Id)))
            .ToListAsync();
    }
}
