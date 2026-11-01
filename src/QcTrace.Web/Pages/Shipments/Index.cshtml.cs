using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;
using QcTrace.Web.Services;

namespace QcTrace.Web.Pages.Shipments;

/// <summary>출하 실적. 승인 잔량(예약)이 있는 LOT만 보여주고, 초과 출하는 usp_ShipLot이 거절한다(R7).</summary>
public class IndexModel(QcTraceDbContext db, QcCommands qc) : QcPageModel
{
    public List<Lot> Lots { get; private set; } = [];
    public List<Shipment> Recent { get; private set; } = [];

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync(int lotId, int qty, string? destination, string requestKey)
    {
        var result = await TryRunAsync(() => qc.ShipLotAsync(requestKey, lotId, CurrentUserId, qty, destination));
        if (result is not null)
        {
            StatusMessage = $"{qty}개 출하를 기록했습니다." + (result.Replayed ? " (이미 처리된 요청)" : "");
            return RedirectToPage();
        }
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Lots = await db.Lots.AsNoTracking().Include(l => l.Item).Include(l => l.Quantity)
            .Where(l => l.Quantity!.Reserved > 0)
            .OrderBy(l => l.CreatedAt)
            .ToListAsync();
        Recent = await db.Shipments.AsNoTracking().OrderByDescending(s => s.CreatedAt).Take(10).ToListAsync();
    }
}
