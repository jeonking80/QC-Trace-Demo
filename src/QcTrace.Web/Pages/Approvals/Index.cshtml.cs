using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;
using QcTrace.Web.Services;

namespace QcTrace.Web.Pages.Approvals;

/// <summary>
/// 출하 승인. 출하 가능(합격 − 출하 − 예약) 수량이 있는 LOT을 보여준다.
/// 내가 검사한 LOT은 버튼을 막고 안내한다(R5). 막는 것은 편의이고, 판단은 usp_ApproveShipment가 한다.
/// </summary>
public class IndexModel(QcTraceDbContext db, QcCommands qc) : QcPageModel
{
    public sealed record Row(Lot Lot, int Available, bool InspectedByMe);

    public List<Row> Rows { get; private set; } = [];

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostApproveAsync(int lotId, int qty, string requestKey)
    {
        var result = await TryRunAsync(() => qc.ApproveShipmentAsync(requestKey, lotId, CurrentUserId, qty, ApprovalDecision.Approved, null));
        if (result is not null)
        {
            StatusMessage = $"{qty}개 출하를 승인했습니다." + (result.Replayed ? " (이미 처리된 요청)" : "");
            return RedirectToPage();
        }
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRejectAsync(int lotId, string? reason, string requestKey)
    {
        var result = await TryRunAsync(() => qc.ApproveShipmentAsync(requestKey, lotId, CurrentUserId, 0, ApprovalDecision.Rejected, reason));
        if (result is not null)
        {
            StatusMessage = "출하를 반려했습니다.";
            return RedirectToPage();
        }
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var me = CurrentUserId;
        Rows = await db.Lots.AsNoTracking()
            .Where(l => l.Quantity!.Passed - l.Quantity.Shipped - l.Quantity.Reserved > 0)
            .OrderBy(l => l.CreatedAt)
            .Select(l => new Row(
                l,
                l.Quantity!.Passed - l.Quantity.Shipped - l.Quantity.Reserved,
                db.Inspections.Any(i => i.LotId == l.Id && i.InspectorId == me)))
            .ToListAsync();
        // Include는 Select 투영에서 무시되므로 품목·수량을 따로 채운다
        var ids = Rows.Select(r => r.Lot.Id).ToList();
        var lots = await db.Lots.AsNoTracking().Include(l => l.Item).Include(l => l.Quantity)
            .Where(l => ids.Contains(l.Id)).ToDictionaryAsync(l => l.Id);
        Rows = Rows.Select(r => r with { Lot = lots[r.Lot.Id] }).ToList();
    }
}
