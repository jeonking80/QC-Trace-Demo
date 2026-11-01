using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Auth;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;
using QcTrace.Web.Data.Seed;
using QcTrace.Web.Services;

namespace QcTrace.Web.Api;

/// <summary>
/// 운영 대시보드 데이터. 화면(wwwroot/js/dashboard.js)은 이 JSON만 받아 그린다(React 없이 fetch).
/// 조회만 하고, 수치는 DB의 현재 수량·대사 뷰·일일 집계 뷰에서 바로 가져온다.
/// </summary>
public static class DashboardEndpoints
{
    public static TimeSpan DelayThreshold => QcQueries.DelayThreshold;
    public const int MaxActions = 50;

    public sealed record LotRef(int LotId, string LotNo, string ItemCode, string ItemName);
    public sealed record Rate(int Inspected, int Failed, decimal? FailRate);
    public sealed record DailyRow(DateOnly Date, int Inspected, int Failed, decimal? FailRate, int Shipped);
    public sealed record ActionItem(string Kind, string Severity, string Title, string Detail, string? LotNo, string Owner, string Href);

    private sealed class SummaryRow
    {
        public DateTime SummaryDate { get; set; }
        public int InspectedQty { get; set; }
        public int FailedQty { get; set; }
        public int ShippedQty { get; set; }
        public int ShipmentCount { get; set; }
    }

    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/dashboard", GetAsync).RequireAuthorization(Policies.Admin);
        return app;
    }

    private static async Task<IResult> GetAsync(QcTraceDbContext db, ClaimsPrincipal user)
    {
        var now = DateTime.UtcNow;
        var today = Kst.Today;
        var from = today.AddDays(-6);

        var open = await db.Lots.AsNoTracking()
            .Where(l => l.Status != LotStatus.Shipped)
            .Select(l => new
            {
                l.Id, l.LotNo, l.Status, l.InspectionRequestedAt, l.Item.ItemCode, ItemName = l.Item.Name,
                q = l.Quantity!, l.ScheduleState, l.ProposedDueAt
            })
            .ToListAsync();

        var waiting = open.Where(l => (l.Status == LotStatus.Waiting || l.Status == LotStatus.Inspecting) && l.q.Uninspected > 0).ToList();
        var delayed = await QcQueries.DelayedLotsAsync(db, now);

        var summary = await db.Database.SqlQuery<SummaryRow>($"""
            SELECT CAST(SummaryDate AS datetime2) AS SummaryDate, InspectedQty, FailedQty, ShippedQty, ShipmentCount
            FROM dbo.vw_DailyQcSummary
            WHERE SummaryDate >= {from.ToDateTime(TimeOnly.MinValue)} AND SummaryDate <= {today.ToDateTime(TimeOnly.MinValue)}
            """).ToListAsync();
        var byDate = summary.ToDictionary(s => DateOnly.FromDateTime(s.SummaryDate));
        var daily = Enumerable.Range(0, 7).Select(i => from.AddDays(i)).Select(d =>
        {
            byDate.TryGetValue(d, out var s);
            return new DailyRow(d, s?.InspectedQty ?? 0, s?.FailedQty ?? 0, RateOf(s?.InspectedQty ?? 0, s?.FailedQty ?? 0), s?.ShippedQty ?? 0);
        }).ToList();
        byDate.TryGetValue(today, out var t);

        var mismatches = await db.Database.SqlQuery<string>($"""
            SELECT LotNo AS Value FROM dbo.vw_LotReconciliation WHERE State = N'Mismatch'
            """).ToListAsync();

        // 불합격 처리 현황(집 PC 피드백 B6): 처리 대기 불합격 수량(모든 LOT), 오늘 폐기·재검사로 돌린 수량
        var todayFromUtc = Kst.StartOfDayUtc(today);
        var tomorrowFromUtc = Kst.StartOfDayUtc(today.AddDays(1));   // 일별 뷰와 같은 기준: 오늘 00:00 이상, 내일 00:00 미만
        var pendingFailed = await db.LotQuantities.AsNoTracking().Where(q => q.Failed > 0)
            .GroupBy(_ => 1).Select(g => new { Qty = g.Sum(q => q.Failed), Lots = g.Count() }).FirstOrDefaultAsync();
        var disposedToday = await db.Ledger.AsNoTracking()
            .Where(l => l.CreatedAt >= todayFromUtc && l.CreatedAt < tomorrowFromUtc && l.Delta > 0
                        && ((l.Kind == LedgerKind.Scrap && l.DeltaField == "Scrapped") || (l.Kind == LedgerKind.Reinspect && l.DeltaField == "Uninspected")))
            .GroupBy(l => l.Kind).Select(g => new { Kind = g.Key, Qty = g.Sum(l => l.Delta) }).ToListAsync();

        var actions = BuildActions(open.Select(l => (l.Id, l.LotNo, l.Status, l.ItemCode, l.q, l.InspectionRequestedAt)).ToList(),
            delayed, mismatches, user, now,
            open.Where(l => l.ScheduleState is ScheduleState.Proposed or ScheduleState.Escalated && l.q.Uninspected > 0)
                .Select(l => (l.LotNo, l.ScheduleState!, l.ProposedDueAt)).ToList());

        return Results.Json(new
        {
            generatedAt = now,
            today,
            waiting = new { lots = waiting.Count, qty = waiting.Sum(l => l.q.Uninspected) },
            delayed = new { thresholdHours = (int)DelayThreshold.TotalHours, count = delayed.Count, lots = delayed.Take(20) },
            failRate = new
            {
                today = new Rate(t?.InspectedQty ?? 0, t?.FailedQty ?? 0, RateOf(t?.InspectedQty ?? 0, t?.FailedQty ?? 0)),
                last7Days = new Rate(daily.Sum(d => d.Inspected), daily.Sum(d => d.Failed), RateOf(daily.Sum(d => d.Inspected), daily.Sum(d => d.Failed))),
                daily,
            },
            shippedToday = new { qty = t?.ShippedQty ?? 0, count = t?.ShipmentCount ?? 0 },
            failedHandling = new
            {
                pendingQty = pendingFailed?.Qty ?? 0,
                pendingLots = pendingFailed?.Lots ?? 0,
                scrappedToday = disposedToday.FirstOrDefault(x => x.Kind == LedgerKind.Scrap)?.Qty ?? 0,
                reinspectedToday = disposedToday.FirstOrDefault(x => x.Kind == LedgerKind.Reinspect)?.Qty ?? 0,
            },
            reconciliation = new { mismatches = mismatches.Count },
            actions = actions.Take(MaxActions),
            actionTotal = actions.Count,
        });
    }

    private static decimal? RateOf(int inspected, int failed) => inspected == 0 ? null : Math.Round((decimal)failed / inspected, 4);

    /// <summary>
    /// 조치 목록. 위험도 순(대사 불일치 → 일정 결정 필요 → 검사 지연 → 보류·불합격 처리 → 승인·출하 대기 → 일정 회신 확인 → 의뢰 대기).
    /// 클릭하면 그 일을 처리하는 화면으로 간다. 보는 사람에게 그 화면 역할이 없으면 해당 LOT 상세로 간다.
    /// </summary>
    private static List<ActionItem> BuildActions(
        List<(int Id, string LotNo, string Status, string ItemCode, LotQuantity Q, DateTime? RequestedAt)> lots,
        List<QcQueries.DelayedLot> delayed, List<string> mismatches, ClaimsPrincipal user, DateTime now,
        List<(string LotNo, string State, DateTime? ProposedDueAt)>? schedules = null)
    {
        // 보는 사람에게 처리 역할이 없으면 LOT 상세로 간다(집 PC 피드백 B1)
        string Ledger(string lotNo) => "/Trace/Lot/" + Uri.EscapeDataString(lotNo);
        string Work(string role, string path, string lotNo) => user.IsInRole(role) ? path : Ledger(lotNo);

        var list = new List<ActionItem>();
        list.AddRange(mismatches.Select(no => new ActionItem("mismatch", "danger", "재고 대사 불일치", "현재 수량과 원장·생산 등식이 맞지 않습니다", no, "관리자", "/Admin/Reconcile")));
        // 검사 일정: 생산 담당이 거절한 회신은 결정권자(관리자·승인권자)에게 올라온다
        foreach (var sc in (schedules ?? []).Where(x => x.State == ScheduleState.Escalated))
            list.Add(new ActionItem("schedule", "danger", "일정 결정 필요", $"검사자 회신({Kst.Format(sc.ProposedDueAt)}까지)이 결정권자 확인 대기(거절 또는 확정 기한 변경)", sc.LotNo, "관리자·승인권자",
                user.IsInRole(Roles.Admin) || user.IsInRole(Roles.Approver) ? "/Schedule" : Ledger(sc.LotNo)));
        list.AddRange(delayed.Select(d => new ActionItem("delayed", "danger", d.Urgent ? "검사 지연 (긴급)" : "검사 지연", $"{d.Describe(now)}, 미검사 {d.Uninspected}개", d.LotNo, "검사자",
            Work(Roles.Inspector, $"/Inspections/Input/{d.LotId}", d.LotNo))));
        foreach (var l in lots)
        {
            if (l.Q.Held > 0)
                list.Add(new ActionItem("held", "warning", "보류 판정 필요", $"보류 {l.Q.Held}개", l.LotNo, "검사자", Work(Roles.Inspector, "/Inspections", l.LotNo)));
            if (l.Q.Failed > 0)
                list.Add(new ActionItem("failed", "warning", "불합격 처리 대기", $"불합격 {l.Q.Failed}개 (재검사는 검사자·폐기는 관리자)", l.LotNo, "검사자·관리자",
                    user.IsInRole(Roles.Admin) ? "/Admin/Scrap" : Work(Roles.Inspector, $"/Inspections/Dispose/{l.Id}", l.LotNo)));
        }
        foreach (var l in lots)
        {
            var available = l.Q.Passed - l.Q.Shipped - l.Q.Reserved;
            if (available > 0)
                list.Add(new ActionItem("approve", "info", "출하 승인 대기", $"출하 가능 {available}개", l.LotNo, "승인권자", Work(Roles.Approver, "/Approvals", l.LotNo)));
            if (l.Q.Reserved > 0)
                list.Add(new ActionItem("ship", "info", "출하 대기", $"승인 잔량 {l.Q.Reserved}개", l.LotNo, "출하 담당", Work(Roles.Shipper, "/Shipments", l.LotNo)));
        }
        foreach (var sc in (schedules ?? []).Where(x => x.State == ScheduleState.Proposed))
            list.Add(new ActionItem("schedule", "info", "일정 회신 확인 대기", $"검사자 회신 {Kst.Format(sc.ProposedDueAt)}까지 (수락·거절)", sc.LotNo, "생산 담당",
                Work(Roles.Producer, "/Lots", sc.LotNo)));
        foreach (var l in lots.Where(l => l.Status == LotStatus.Registered))
            list.Add(new ActionItem("request", "info", "검사 의뢰 대기", "등록만 되고 검사 의뢰 전입니다", l.LotNo, "생산 담당", Work(Roles.Producer, "/Lots?status=Registered", l.LotNo)));
        return list;
    }
}
