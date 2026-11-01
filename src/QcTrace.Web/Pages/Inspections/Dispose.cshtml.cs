using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;
using QcTrace.Web.Services;

namespace QcTrace.Web.Pages.Inspections;

/// <summary>불합격 처리: 재검사 요청(불합격 → 미검사) 또는 폐기(불합격 → 폐기). 사유 필수(usp_DisposeFailed).</summary>
public class DisposeModel(QcTraceDbContext db, QcCommands qc) : QcPageModel
{
    public Lot Lot { get; private set; } = null!;
    public List<Inspection> FailedInspections { get; private set; } = [];

    [BindProperty] public string RequestKey { get; set; } = NewRequestKey();
    [BindProperty] public int Qty { get; set; }
    [BindProperty] public string Action { get; set; } = "Reinspect";
    [BindProperty] public string? Reason { get; set; }

    public async Task<IActionResult> OnGetAsync(int lotId)
    {
        if (!await LoadAsync(lotId)) return NotFound();
        Qty = Lot.Quantity!.Failed;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int lotId)
    {
        if (!await LoadAsync(lotId)) return NotFound();

        var result = await TryRunAsync(() => qc.DisposeFailedAsync(RequestKey, lotId, CurrentUserId, Qty, Action, Reason));
        if (result is null) return Page();

        StatusMessage = $"{Lot.LotNo} 불합격 {Qty}개를 {(Action == "Reinspect" ? "재검사 대기(미검사)로 되돌렸습니다" : "폐기로 확정했습니다")}.";
        return RedirectToPage("./Index");
    }

    private async Task<bool> LoadAsync(int lotId)
    {
        var lot = await db.Lots.AsNoTracking().Include(l => l.Item).Include(l => l.Quantity).FirstOrDefaultAsync(l => l.Id == lotId);
        if (lot is null) return false;
        Lot = lot;
        FailedInspections = await db.Inspections.AsNoTracking()
            .Where(i => i.LotId == lotId && i.Verdict == Verdict.Fail)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();
        return true;
    }
}
