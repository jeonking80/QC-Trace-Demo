using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;
using QcTrace.Web.Services;

namespace QcTrace.Web.Pages.Admin;

/// <summary>품목 한 건: 이름·규격 설명 수정, 검사 항목(하한·상한) 추가·수정</summary>
public class ItemModel(QcTraceDbContext db) : QcPageModel
{
    public Item Item { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(int id) => await LoadAsync(id) ? Page() : NotFound();

    public async Task<IActionResult> OnPostItemAsync(int id, string? name, string? spec)
    {
        var item = await db.Items.FindAsync(id);
        if (item is null) return NotFound();
        var errors = MasterData.ValidateItem(item.ItemCode, name, spec).ToList();
        if (errors.Count > 0) return await Fail(id, errors);
        item.Name = name!.Trim();
        item.Spec = string.IsNullOrWhiteSpace(spec) ? null : spec.Trim();
        await db.SaveChangesAsync();
        StatusMessage = "품목 정보를 저장했습니다.";
        return RedirectToPage(new { id });
    }

    /// <summary>inspectionItemId가 없으면 추가, 있으면 수정</summary>
    public async Task<IActionResult> OnPostInspectionItemAsync(int id, int? inspectionItemId, string? name, string? lower, string? upper, string? unit, int? sortOrder)
    {
        var item = await db.Items.Include(i => i.InspectionItems).FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return NotFound();

        var errors = new List<string>();
        decimal? lo = Parse(lower, "하한", errors), up = Parse(upper, "상한", errors);
        if (errors.Count == 0) errors.AddRange(MasterData.ValidateInspectionItem(name, lo, up, unit, sortOrder ?? 0));
        var trimmed = name?.Trim() ?? "";
        if (item.InspectionItems.Any(x => x.Name == trimmed && x.Id != inspectionItemId))
            errors.Add($"검사 항목 {trimmed}이 이미 있습니다.");

        var target = inspectionItemId is null ? null : item.InspectionItems.FirstOrDefault(x => x.Id == inspectionItemId);
        if (inspectionItemId is not null && target is null) return NotFound();
        if (errors.Count > 0) return await Fail(id, errors);

        if (target is null)
        {
            target = new InspectionItem();
            item.InspectionItems.Add(target);
        }
        target.Name = trimmed;
        target.Lower = lo!.Value;
        target.Upper = up!.Value;
        target.Unit = unit?.Trim() ?? "";
        target.SortOrder = sortOrder ?? 0;
        await db.SaveChangesAsync();
        StatusMessage = $"검사 항목 {trimmed}을 저장했습니다. 새 규격은 이후 검사부터 적용됩니다.";
        return RedirectToPage(new { id });
    }

    private static decimal? Parse(string? raw, string label, List<string> errors)
    {
        if (decimal.TryParse(raw?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return d;
        errors.Add($"{label}은 숫자여야 합니다.");
        return null;
    }

    private async Task<IActionResult> Fail(int id, List<string> errors)
    {
        errors.ForEach(e => ModelState.AddModelError("", e));
        await LoadAsync(id);
        return Page();
    }

    private async Task<bool> LoadAsync(int id)
    {
        var item = await db.Items.AsNoTracking().Include(i => i.InspectionItems.OrderBy(x => x.SortOrder)).FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return false;
        Item = item;
        return true;
    }
}
