using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;

namespace QcTrace.Web.Pages.Inspections;

/// <summary>검사자 작업 목록: 검사할 LOT(대기·부분 검사)과 불합격 처리할 LOT. 오래 기다린 LOT이 위로 온다.</summary>
public class IndexModel(QcTraceDbContext db) : QcPageModel
{
    public static readonly TimeSpan DelayThreshold = TimeSpan.FromHours(48);

    public List<Lot> ToInspect { get; private set; } = [];
    public List<Lot> ToDispose { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var lots = db.Lots.AsNoTracking().Include(l => l.Item).Include(l => l.Quantity);
        ToInspect = await lots
            .Where(l => (l.Status == LotStatus.Waiting || l.Status == LotStatus.Inspecting) && l.Quantity!.Uninspected > 0)
            .OrderBy(l => l.InspectionRequestedAt)
            .ToListAsync();
        ToDispose = await lots
            .Where(l => l.Quantity!.Failed > 0)
            .OrderBy(l => l.CreatedAt)
            .ToListAsync();
    }

    public static bool IsDelayed(Lot lot) =>
        lot.InspectionRequestedAt is { } at && DateTime.UtcNow - at > DelayThreshold;
}
