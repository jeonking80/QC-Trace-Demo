using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;

namespace QcTrace.Web.Services;

/// <summary>대시보드·보고서·지연 알림이 같이 쓰는 조회. 정의를 한곳에 둔다.</summary>
public static class QcQueries
{
    /// <summary>검사 지연 기준: 검사 의뢰 후 48시간이 지나도 미검사가 남음</summary>
    public static readonly TimeSpan DelayThreshold = TimeSpan.FromHours(48);

    public sealed record DelayedLot(int LotId, string LotNo, string ItemCode, string ItemName, DateTime RequestedAt, int Hours, int Uninspected);

    public static async Task<List<DelayedLot>> DelayedLotsAsync(QcTraceDbContext db, DateTime nowUtc, CancellationToken ct = default)
    {
        var cutoff = nowUtc - DelayThreshold;
        var rows = await db.Lots.AsNoTracking()
            .Where(l => (l.Status == LotStatus.Waiting || l.Status == LotStatus.Inspecting)
                        && l.Quantity!.Uninspected > 0 && l.InspectionRequestedAt < cutoff)
            .OrderBy(l => l.InspectionRequestedAt)
            .Select(l => new { l.Id, l.LotNo, l.Item.ItemCode, l.Item.Name, At = l.InspectionRequestedAt!.Value, l.Quantity!.Uninspected })
            .ToListAsync(ct);
        return rows.Select(r => new DelayedLot(r.Id, r.LotNo, r.ItemCode, r.Name, r.At, (int)(nowUtc - r.At).TotalHours, r.Uninspected)).ToList();
    }
}
