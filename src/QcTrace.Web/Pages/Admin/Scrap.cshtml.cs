using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;
using QcTrace.Web.Services;

namespace QcTrace.Web.Pages.Admin;

/// <summary>
/// 불합격 폐기(관리자만, 2026-11-01 사용자 결정 "폐기는 관리자만"). 불합격이 남은 LOT과 불합격 사유를 보고
/// 수량·사유를 넣어 폐기로 확정한다(usp_DisposeFailed Scrap). 재검사 요청은 검사자 화면에서 한다.
/// </summary>
public class ScrapModel(QcTraceDbContext db, QcCommands qc) : QcPageModel
{
    public sealed record Row(Lot Lot, List<(DateTime At, int Qty, string? Reason, string Inspector)> Fails);
    public List<Row> Rows { get; private set; } = [];

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync(int lotId, int qty, string? reason, string requestKey)
    {
        var result = await TryRunAsync(() => qc.DisposeFailedAsync(requestKey, lotId, CurrentUserId, qty, "Scrap", reason));
        if (result is not null)
        {
            var lotNo = await db.Lots.Where(l => l.Id == lotId).Select(l => l.LotNo).FirstAsync();
            StatusMessage = $"{lotNo} 불합격 {qty}개를 폐기로 확정했습니다.";
            return RedirectToPage();
        }
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var lots = await db.Lots.AsNoTracking().Include(l => l.Item).Include(l => l.Quantity)
            .Where(l => l.Quantity!.Failed > 0).OrderBy(l => l.CreatedAt).ToListAsync();
        var ids = lots.Select(l => l.Id).ToList();
        var fails = await (
            from i in db.Inspections.AsNoTracking()
            join u in db.Users on i.InspectorId equals u.Id
            where ids.Contains(i.LotId) && i.Verdict == Verdict.Fail
            orderby i.CreatedAt descending
            select new { i.LotId, i.CreatedAt, i.Qty, i.Reason, u.DisplayName }).ToListAsync();
        Rows = lots.Select(l => new Row(l, fails.Where(f => f.LotId == l.Id)
            .Select(f => (At: f.CreatedAt, Qty: f.Qty, Reason: (string?)f.Reason, Inspector: f.DisplayName)).ToList())).ToList();
    }
}
