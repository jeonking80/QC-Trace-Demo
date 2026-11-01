using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;

namespace QcTrace.Web.Services;

/// <summary>대시보드·보고서·지연 알림이 같이 쓰는 조회. 정의를 한곳에 둔다.</summary>
public static class QcQueries
{
    /// <summary>
    /// 검사 지연 기준: 검사 기한(의뢰 때 희망 기한, 일정 수락·결정으로 바뀐 기한)이 있으면 그 기한이 지나도,
    /// 없으면 검사 의뢰 후 48시간이 지나도 미검사가 남음
    /// </summary>
    public static readonly TimeSpan DelayThreshold = TimeSpan.FromHours(48);

    /// <summary>Hours: 의뢰 후 경과 시간. DueAt이 있으면 OverdueHours가 기한 초과 시간이다.</summary>
    public sealed record DelayedLot(int LotId, string LotNo, string ItemCode, string ItemName, DateTime RequestedAt, int Hours, int Uninspected,
        DateTime? DueAt = null, bool Urgent = false)
    {
        public int? OverdueHours(DateTime nowUtc) => DueAt is { } due ? (int)(nowUtc - due).TotalHours : null;

        /// <summary>"의뢰 후 50시간" 또는 "기한 3시간 초과 (의뢰 후 20시간)"</summary>
        public string Describe(DateTime nowUtc) => DueAt is { } due
            ? $"기한 {Schedule.Remaining(due, nowUtc)} (의뢰 후 {Hours}시간)"
            : $"의뢰 후 {Hours}시간";
    }

    public static bool IsDelayed(Lot lot, DateTime nowUtc) =>
        lot.InspectionRequestedAt is { } at
        && (lot.DueAt is { } due ? due < nowUtc : at < nowUtc - DelayThreshold);

    public static async Task<List<DelayedLot>> DelayedLotsAsync(QcTraceDbContext db, DateTime nowUtc, CancellationToken ct = default)
    {
        var cutoff = nowUtc - DelayThreshold;
        var rows = await db.Lots.AsNoTracking()
            .Where(l => (l.Status == LotStatus.Waiting || l.Status == LotStatus.Inspecting)
                        && l.Quantity!.Uninspected > 0 && l.InspectionRequestedAt != null
                        && (l.DueAt != null ? l.DueAt < nowUtc : l.InspectionRequestedAt < cutoff))
            .OrderBy(l => l.InspectionRequestedAt)
            .Select(l => new { l.Id, l.LotNo, l.Item.ItemCode, l.Item.Name, At = l.InspectionRequestedAt!.Value, l.Quantity!.Uninspected, l.DueAt, l.Priority })
            .ToListAsync(ct);
        return rows.Select(r => new DelayedLot(r.Id, r.LotNo, r.ItemCode, r.Name, r.At, (int)(nowUtc - r.At).TotalHours, r.Uninspected,
            r.DueAt, r.Priority == LotPriority.Urgent)).ToList();
    }
}
