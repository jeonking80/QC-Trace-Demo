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
    public static readonly TimeSpan DelayThreshold = TimeSpan.FromHours(48);
    public const int MaxActions = 50;

    public sealed record LotRef(int LotId, string LotNo, string ItemCode, string ItemName);
    public sealed record DelayedLot(int LotId, string LotNo, string ItemCode, string ItemName, DateTime RequestedAt, int Hours, int Uninspected);
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
                q = l.Quantity!
            })
            .ToListAsync();

        var waiting = open.Where(l => (l.Status == LotStatus.Waiting || l.Status == LotStatus.Inspecting) && l.q.Uninspected > 0).ToList();
        var delayed = waiting
            .Where(l => l.InspectionRequestedAt is { } at && now - at > DelayThreshold)
            .OrderBy(l => l.InspectionRequestedAt)
            .Select(l => new DelayedLot(l.Id, l.LotNo, l.ItemCode, l.ItemName, l.InspectionRequestedAt!.Value,
                (int)(now - l.InspectionRequestedAt!.Value).TotalHours, l.q.Uninspected))
            .ToList();

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

        var actions = BuildActions(open.Select(l => (l.Id, l.LotNo, l.Status, l.ItemCode, l.q, l.InspectionRequestedAt)).ToList(),
            delayed, mismatches, user, now);

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
            reconciliation = new { mismatches = mismatches.Count },
            actions = actions.Take(MaxActions),
            actionTotal = actions.Count,
        });
    }

    private static decimal? RateOf(int inspected, int failed) => inspected == 0 ? null : Math.Round((decimal)failed / inspected, 4);

    /// <summary>
    /// 조치 목록. 위험도 순(대사 불일치 → 검사 지연 → 보류·불합격 처리 → 승인·출하 대기 → 의뢰 대기).
    /// 클릭하면 그 일을 처리하는 화면으로 간다. 보는 사람에게 그 화면 역할이 없으면 해당 LOT의 이력 원장으로 간다.
    /// </summary>
    private static List<ActionItem> BuildActions(
        List<(int Id, string LotNo, string Status, string ItemCode, LotQuantity Q, DateTime? RequestedAt)> lots,
        List<DelayedLot> delayed, List<string> mismatches, ClaimsPrincipal user, DateTime now)
    {
        string Ledger(string lotNo) => "/Admin/Ledger?LotNo=" + Uri.EscapeDataString(lotNo);
        string Work(string role, string path, string lotNo) => user.IsInRole(role) ? path : Ledger(lotNo);

        var list = new List<ActionItem>();
        list.AddRange(mismatches.Select(no => new ActionItem("mismatch", "danger", "재고 대사 불일치", "현재 수량과 원장·생산 등식이 맞지 않습니다", no, "관리자", "/Admin/Reconcile")));
        list.AddRange(delayed.Select(d => new ActionItem("delayed", "danger", "검사 지연", $"의뢰 후 {d.Hours}시간, 미검사 {d.Uninspected}개", d.LotNo, "검사자",
            Work(Roles.Inspector, $"/Inspections/Input/{d.LotId}", d.LotNo))));
        foreach (var l in lots)
        {
            if (l.Q.Held > 0)
                list.Add(new ActionItem("held", "warning", "보류 판정 필요", $"보류 {l.Q.Held}개", l.LotNo, "검사자", Work(Roles.Inspector, "/Inspections", l.LotNo)));
            if (l.Q.Failed > 0)
                list.Add(new ActionItem("failed", "warning", "불합격 처리 대기", $"불합격 {l.Q.Failed}개 (재검사·폐기)", l.LotNo, "검사자",
                    Work(Roles.Inspector, $"/Inspections/Dispose/{l.Id}", l.LotNo)));
        }
        foreach (var l in lots)
        {
            var available = l.Q.Passed - l.Q.Shipped - l.Q.Reserved;
            if (available > 0)
                list.Add(new ActionItem("approve", "info", "출하 승인 대기", $"출하 가능 {available}개", l.LotNo, "승인권자", Work(Roles.Approver, "/Approvals", l.LotNo)));
            if (l.Q.Reserved > 0)
                list.Add(new ActionItem("ship", "info", "출하 대기", $"승인 잔량 {l.Q.Reserved}개", l.LotNo, "출하 담당", Work(Roles.Shipper, "/Shipments", l.LotNo)));
        }
        foreach (var l in lots.Where(l => l.Status == LotStatus.Registered))
            list.Add(new ActionItem("request", "info", "검사 의뢰 대기", "등록만 되고 검사 의뢰 전입니다", l.LotNo, "생산 담당", Work(Roles.Producer, "/Lots?status=Registered", l.LotNo)));
        return list;
    }
}
