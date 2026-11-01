using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;
using QcTrace.Web.Services;

namespace QcTrace.Web.Pages.Trace;

/// <summary>
/// LOT 상세(집 PC 피드백 B1·B2). 역할이 있는 사용자 누구나 본다(조회 전용).
/// 수량 현황, 다음 할 일, 검사(사유·측정값·검사자), 승인·출하, 불합격 처리, 일어난 순서대로의 이력을 한 화면에 모은다.
/// </summary>
public class LotModel(QcTraceDbContext db) : QcPageModel
{
    public Lot Lot { get; private set; } = null!;
    public List<LotNextStep.Step> NextSteps { get; private set; } = [];
    public List<InspectionView> Inspections { get; private set; } = [];
    public List<ApprovalView> Approvals { get; private set; } = [];
    public List<ShipmentView> Shipments { get; private set; } = [];
    public List<LedgerText.Event> Dispositions { get; private set; } = [];
    public List<LedgerText.Event> Timeline { get; private set; } = [];
    public List<string> Inspectors { get; private set; } = [];

    public sealed record Measurement(string Name, string Value, string Spec, bool? InSpec);
    public sealed record InspectionView(long Id, DateTime CreatedAt, string Inspector, int Qty, string Verdict, string? Reason,
        long? CorrectsInspectionId, List<Measurement> Measurements);
    public sealed record ApprovalView(DateTime CreatedAt, string Approver, string Decision, int Qty, string? Reason);
    public sealed record ShipmentView(DateTime CreatedAt, string Shipper, int Qty, string? Destination);

    public async Task<IActionResult> OnGetAsync(string lotNo)
    {
        var lot = await db.Lots.AsNoTracking().Include(l => l.Item).Include(l => l.Quantity)
            .FirstOrDefaultAsync(l => l.LotNo == lotNo);
        if (lot is null) return NotFound();
        Lot = lot;
        NextSteps = LotNextStep.For(lot, lot.Quantity);

        var specs = await db.InspectionItems.AsNoTracking().Where(i => i.ItemId == lot.ItemId).ToListAsync();
        var inspections = await (
            from i in db.Inspections.AsNoTracking()
            join u in db.Users on i.InspectorId equals u.Id
            where i.LotId == lot.Id
            orderby i.CreatedAt, i.Id
            select new { i, Name = u.DisplayName + " (" + u.UserName + ")" }).ToListAsync();
        Inspections = inspections.Select(x => new InspectionView(x.i.Id, x.i.CreatedAt, x.Name, x.i.Qty, x.i.Verdict, x.i.Reason,
            x.i.CorrectsInspectionId, ParseMeasurements(x.i.MeasuredJson, specs))).ToList();
        Inspectors = inspections.Select(x => x.Name).Distinct().ToList();

        Approvals = await (
            from a in db.ShipmentApprovals.AsNoTracking()
            join u in db.Users on a.ApproverId equals u.Id
            where a.LotId == lot.Id
            orderby a.CreatedAt
            select new ApprovalView(a.CreatedAt, u.DisplayName + " (" + u.UserName + ")", a.Decision, a.ApprovedQty, a.Reason)).ToListAsync();
        Shipments = await (
            from s in db.Shipments.AsNoTracking()
            join u in db.Users on s.ShipperId equals u.Id
            where s.LotId == lot.Id
            orderby s.CreatedAt
            select new ShipmentView(s.CreatedAt, u.DisplayName + " (" + u.UserName + ")", s.Qty, s.Destination)).ToListAsync();

        var lines = await (
            from l in db.Ledger.AsNoTracking()
            join u in db.Users on l.ActorId equals u.Id
            where l.LotId == lot.Id
            select new LedgerText.Line(l.Id, l.CreatedAt, lot.LotNo, l.Kind, l.DeltaField, l.Delta,
                u.DisplayName + " (" + u.UserName + ")", l.Reason, l.SourceTable, l.SourceId)).ToListAsync();
        Timeline = LedgerText.Group(lines);
        Dispositions = Timeline.Where(e => e.Kind is LedgerKind.Reinspect or LedgerKind.Scrap).ToList();
        return Page();
    }

    private static List<Measurement> ParseMeasurements(string? json, List<InspectionItem> specs)
    {
        var result = new List<Measurement>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var m in doc.RootElement.EnumerateArray())
            {
                InspectionItem? spec = null;
                if (m.TryGetProperty("inspectionItemId", out var id) && id.TryGetInt32(out var itemId)) spec = specs.FirstOrDefault(s => s.Id == itemId);
                var name = m.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : spec?.Name ?? "?";
                spec ??= specs.FirstOrDefault(s => s.Name == name);
                var raw = m.TryGetProperty("value", out var v) ? v.ToString() : "";
                bool? inSpec = spec is not null && decimal.TryParse(raw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d)
                    ? d >= spec.Lower && d <= spec.Upper : null;
                var spectext = spec is null ? "" : $"{spec.Lower:0.###} ~ {spec.Upper:0.###} {spec.Unit}";
                result.Add(new Measurement(name, raw, spectext, inSpec));
            }
        }
        catch (JsonException)
        {
            result.Add(new Measurement("측정값", json, "", null));
        }
        return result;
    }

    public static string VerdictLabel(string v) => v switch { Verdict.Pass => "합격", Verdict.Fail => "불합격", Verdict.Hold => "보류", _ => v };
    public static string VerdictCss(string v) => v switch { Verdict.Pass => "text-success", Verdict.Fail => "text-danger", Verdict.Hold => "text-warning", _ => "" };
}
