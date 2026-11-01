using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;
using QcTrace.Web.Services;

namespace QcTrace.Web.Pages.Admin;

/// <summary>
/// 불량 유형 기준정보(2026-11-01 사용자 결정: 목록에서 고르고 "기타"면 직접 입력).
/// 관리자가 추가·사용 중지한다. 검사 기록이 참조하므로 지우지 않는다. 유형별 불량 수량(최근 30일)도 보여 준다.
/// </summary>
public class DefectCodesModel(QcTraceDbContext db) : QcPageModel
{
    public const int StatDays = 30;
    public sealed record Row(DefectCode Code, int FailQty, int HoldQty, int Count);

    public List<Row> Rows { get; private set; } = [];
    /// <summary>유형 없이 남은 불합격·보류(2026-11-01 이전 자유 입력)</summary>
    public (int FailQty, int HoldQty, int Count) Uncoded { get; private set; }

    [BindProperty] public string? NewCode { get; set; }
    [BindProperty] public string? NewName { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAddAsync()
    {
        var code = NewCode?.Trim().ToUpperInvariant();
        var name = NewName?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 20) ModelState.AddModelError("", "코드는 1~20자로 입력하세요.");
        if (string.IsNullOrEmpty(name) || name.Length > 100) ModelState.AddModelError("", "이름은 1~100자로 입력하세요.");
        if (ModelState.ErrorCount == 0 && await db.DefectCodes.AnyAsync(d => d.Code == code))
            ModelState.AddModelError("", $"코드 {code}가 이미 있습니다.");
        if (ModelState.ErrorCount > 0)
        {
            await LoadAsync();
            return Page();
        }
        var max = await db.DefectCodes.Where(d => d.Code != DefectCode.Other).MaxAsync(d => (int?)d.SortOrder) ?? 0;
        db.DefectCodes.Add(new DefectCode { Code = code!, Name = name!, SortOrder = max + 10 });
        await db.SaveChangesAsync();
        StatusMessage = $"불량 유형 {name}({code})을 추가했습니다. 검사 입력 목록에 바로 나옵니다.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var code = await db.DefectCodes.FindAsync(id);
        if (code is null) return NotFound();
        if (code.Code == DefectCode.Other)
        {
            ModelState.AddModelError("", "\"기타\"는 목록에 없는 불량을 적는 칸이라 사용 중지할 수 없습니다.");
            await LoadAsync();
            return Page();
        }
        code.IsActive = !code.IsActive;
        await db.SaveChangesAsync();
        StatusMessage = code.IsActive ? $"{code.Name}을 다시 사용합니다." : $"{code.Name}을 사용 중지했습니다. 지난 기록은 그대로 남습니다.";
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        var since = DateTime.UtcNow.AddDays(-StatDays);
        var stats = await db.Inspections.AsNoTracking()
            .Where(i => i.CreatedAt >= since && i.Verdict != Verdict.Pass && i.CorrectsInspectionId == null)
            .GroupBy(i => i.DefectCodeId)
            .Select(g => new
            {
                CodeId = g.Key,
                Fail = g.Where(i => i.Verdict == Verdict.Fail).Sum(i => (int?)i.Qty) ?? 0,
                Hold = g.Where(i => i.Verdict == Verdict.Hold).Sum(i => (int?)i.Qty) ?? 0,
                Count = g.Count(),
            }).ToListAsync();
        var codes = await db.DefectCodes.AsNoTracking().OrderBy(d => d.SortOrder).ThenBy(d => d.Code).ToListAsync();
        Rows = codes.Select(c =>
        {
            var s = stats.FirstOrDefault(x => x.CodeId == c.Id);
            return new Row(c, s?.Fail ?? 0, s?.Hold ?? 0, s?.Count ?? 0);
        }).ToList();
        var none = stats.FirstOrDefault(x => x.CodeId == null);
        Uncoded = (none?.Fail ?? 0, none?.Hold ?? 0, none?.Count ?? 0);
    }
}
