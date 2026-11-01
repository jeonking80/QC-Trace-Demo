using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;

namespace QcTrace.Web.Pages.Inspections;

/// <summary>검사자 작업 목록: 검사할 LOT(대기·부분 검사)과 불합격 처리할 LOT. 긴급·기한 임박·오래 기다린 LOT이 위로 온다.</summary>
public class IndexModel(QcTraceDbContext db) : QcPageModel
{
    public List<Lot> ToInspect { get; private set; } = [];
    public List<Lot> ToDispose { get; private set; } = [];
    /// <summary>최근 처리 내역: 검사·정정·재검사·폐기(집 PC 피드백 B3). mine=1이면 내가 한 것만</summary>
    [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)] public bool Mine { get; set; }
    public List<QcTrace.Web.Services.LedgerText.Event> Recent { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var lots = db.Lots.AsNoTracking().Include(l => l.Item).Include(l => l.Quantity);
        // 긴급 → 기한 빠른 순 → 의뢰 오래된 순(검사 일정, 2026-11-01 사용자 요청)
        ToInspect = QcTrace.Web.Services.Schedule.WorkOrder(await lots
            .Where(l => (l.Status == LotStatus.Waiting || l.Status == LotStatus.Inspecting) && l.Quantity!.Uninspected > 0)
            .ToListAsync()).ToList();
        ToDispose = await lots
            .Where(l => l.Quantity!.Failed > 0)
            .OrderBy(l => l.CreatedAt)
            .ToListAsync();

        string[] kinds = [LedgerKind.Inspect, LedgerKind.Correct, LedgerKind.Reinspect, LedgerKind.Scrap];
        var me = CurrentUserId;
        var lines = await (
            from l in db.Ledger.AsNoTracking()
            join lot in db.Lots on l.LotId equals lot.Id
            join u in db.Users on l.ActorId equals u.Id
            where kinds.Contains(l.Kind) && (!Mine || l.ActorId == me)
            orderby l.CreatedAt descending, l.Id descending
            select new QcTrace.Web.Services.LedgerText.Line(l.Id, l.CreatedAt, lot.LotNo, l.Kind, l.DeltaField, l.Delta,
                u.DisplayName, l.Reason, l.SourceTable, l.SourceId))
            .Take(120).ToListAsync();
        Recent = QcTrace.Web.Services.LedgerText.Group(lines);
        Recent.Reverse();
        if (Recent.Count > 30) Recent = Recent.Take(30).ToList();
    }

    public static bool IsDelayed(Lot lot) => QcTrace.Web.Services.QcQueries.IsDelayed(lot, DateTime.UtcNow);
}
