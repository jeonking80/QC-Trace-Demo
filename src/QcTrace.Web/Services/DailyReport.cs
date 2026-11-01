using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;

namespace QcTrace.Web.Services;

/// <summary>
/// 일일 검사·출하 엑셀 보고서(ClosedXML). 보고 기준일(한국 날짜) 하루의 실적과, 생성 시점의 지연·대사 상태를 담는다.
/// 파일: {Reports:Path}/qc-daily-yyyyMMdd.xlsx. 임시 파일에 쓴 뒤 이름을 바꿔 반쯤 쓴 파일이 보이지 않게 한다.
/// </summary>
public sealed partial class DailyReport(QcTraceDbContext db, ReportStorage storage, TimeProvider time)
{
    private sealed class SummaryRow
    {
        public int InspectedQty { get; set; }
        public int PassedQty { get; set; }
        public int FailedQty { get; set; }
        public int HeldQty { get; set; }
        public int CorrectionCount { get; set; }
        public int ApprovedQty { get; set; }
        public int RejectedCount { get; set; }
        public int ShippedQty { get; set; }
        public int ShipmentCount { get; set; }
        [Precision(9, 4)] public decimal? FailRate { get; set; }   // 뷰의 decimal(9,4)와 같게(정밀도 미지정 경고 제거)
    }

    private sealed class ReconRow
    {
        public string LotNo { get; set; } = "";
        public string State { get; set; } = "";
        public int ProducedQty { get; set; }
        public int ProductionDiff { get; set; }
        public int OverageQty { get; set; }
        public bool LedgerMatches { get; set; }
    }

    public async Task<ReportStorage.ReportFile> GenerateAsync(DateOnly businessDate, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var fromUtc = Kst.StartOfDayUtc(businessDate);
        var toUtc = Kst.StartOfDayUtc(businessDate.AddDays(1));
        var day = businessDate.ToDateTime(TimeOnly.MinValue);

        var summary = (await db.Database.SqlQuery<SummaryRow>($"""
            SELECT InspectedQty, PassedQty, FailedQty, HeldQty, CorrectionCount, ApprovedQty, RejectedCount, ShippedQty, ShipmentCount, FailRate
            FROM dbo.vw_DailyQcSummary WHERE SummaryDate = {day}
            """).ToListAsync(ct)).SingleOrDefault() ?? new SummaryRow();

        var inspections = await (
            from i in db.Inspections.AsNoTracking()
            join l in db.Lots on i.LotId equals l.Id
            join u in db.Users on i.InspectorId equals u.Id
            where i.CreatedAt >= fromUtc && i.CreatedAt < toUtc
            orderby i.CreatedAt
            select new { i.CreatedAt, l.LotNo, l.Item.ItemCode, Inspector = u.DisplayName, i.Qty, i.Verdict, i.Reason, i.CorrectsInspectionId, i.Id })
            .ToListAsync(ct);

        var approvals = await (
            from a in db.ShipmentApprovals.AsNoTracking()
            join l in db.Lots on a.LotId equals l.Id
            join u in db.Users on a.ApproverId equals u.Id
            where a.CreatedAt >= fromUtc && a.CreatedAt < toUtc
            select new { a.CreatedAt, Kind = a.Decision == ApprovalDecision.Approved ? "출하 승인" : "승인 반려", l.LotNo, Actor = u.DisplayName, Qty = a.ApprovedQty, Note = a.Reason })
            .ToListAsync(ct);
        var shipments = await (
            from s in db.Shipments.AsNoTracking()
            join l in db.Lots on s.LotId equals l.Id
            join u in db.Users on s.ShipperId equals u.Id
            where s.CreatedAt >= fromUtc && s.CreatedAt < toUtc
            select new { s.CreatedAt, Kind = "출하", l.LotNo, Actor = u.DisplayName, s.Qty, Note = (string?)s.Destination })
            .ToListAsync(ct);
        var moves = approvals.Concat(shipments).OrderBy(x => x.CreatedAt).ToList();

        var delayed = await QcQueries.DelayedLotsAsync(db, now, ct);
        var alerts = await db.DelayAlerts.AsNoTracking().ToDictionaryAsync(a => a.LotId, a => a.DetectedAt, ct);
        var recon = await db.Database.SqlQuery<ReconRow>($"""
            SELECT LotNo, State, ProducedQty, ProductionDiff, OverageQty, LedgerMatches FROM dbo.vw_LotReconciliation WHERE State <> N'OK'
            """).OrderBy(r => r.State).ThenBy(r => r.LotNo).ToListAsync(ct);
        var waiting = await db.Lots.AsNoTracking()
            .Where(l => (l.Status == LotStatus.Waiting || l.Status == LotStatus.Inspecting) && l.Quantity!.Uninspected > 0)
            .Select(l => l.Quantity!.Uninspected).ToListAsync(ct);

        using var wb = new XLWorkbook();

        // 1. 요약
        var ws = wb.AddWorksheet("요약");
        ws.Cell(1, 1).Value = "QC Trace 일일 검사·출하 보고서";
        ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);
        var rows = new (string Label, XLCellValue Value, string? Format)[]
        {
            ("보고 기준일", businessDate.ToString("yyyy-MM-dd"), null),
            ("생성 시각(한국)", Kst.FormatSeconds(now), null),
            ("", "", null),
            ("[기준일 실적]", "", null),
            ("검사 수량", summary.InspectedQty, "#,##0"),
            ("합격", summary.PassedQty, "#,##0"),
            ("불합격", summary.FailedQty, "#,##0"),
            ("보류", summary.HeldQty, "#,##0"),
            ("불합격률(처음 판정 기준)", summary.FailRate is { } r ? (double)r : Blank.Value, "0.0%"),
            ("정정 건수", summary.CorrectionCount, "#,##0"),
            ("출하 승인 수량", summary.ApprovedQty, "#,##0"),
            ("승인 반려 건수", summary.RejectedCount, "#,##0"),
            ("출하 수량", summary.ShippedQty, "#,##0"),
            ("출하 건수", summary.ShipmentCount, "#,##0"),
            ("", "", null),
            ("[생성 시점 상태]", "", null),
            ("검사 대기 LOT", waiting.Count, "#,##0"),
            ("검사 대기 미검사 수량", waiting.Sum(), "#,##0"),
            ($"검사 지연 LOT({(int)QcQueries.DelayThreshold.TotalHours}시간 초과)", delayed.Count, "#,##0"),
            ("재고 대사 불일치 LOT", recon.Count(x => x.State == "Mismatch"), "#,##0"),
            ("초과 보류(R9) LOT", recon.Count(x => x.State == "OverageHeld"), "#,##0"),
        };
        for (var i = 0; i < rows.Length; i++)
        {
            ws.Cell(i + 3, 1).Value = rows[i].Label;
            ws.Cell(i + 3, 2).Value = rows[i].Value;
            if (rows[i].Format is { } f) ws.Cell(i + 3, 2).Style.NumberFormat.Format = f;
            if (rows[i].Label.StartsWith('[')) ws.Cell(i + 3, 1).Style.Font.SetBold();
        }
        ws.Column(1).Width = 32;
        ws.Column(2).Width = 22;

        // 2. 검사 실적
        Table(wb.AddWorksheet("검사 실적"), ["시각(한국)", "LOT", "품목", "검사자", "수량", "판정", "사유", "정정 대상 검사 Id", "검사 Id"],
            inspections.Select(i => new XLCellValue[] { Kst.FormatSeconds(i.CreatedAt), i.LotNo, i.ItemCode, i.Inspector, i.Qty, VerdictLabel(i.Verdict),
                i.Reason ?? "", i.CorrectsInspectionId is { } c ? c : Blank.Value, i.Id }),
            [20, 20, 12, 14, 8, 8, 30, 16, 10]);

        // 3. 승인·출하
        Table(wb.AddWorksheet("승인·출하"), ["시각(한국)", "구분", "LOT", "작업자", "수량", "출하처·사유"],
            moves.Select(m => new XLCellValue[] { Kst.FormatSeconds(m.CreatedAt), m.Kind, m.LotNo, m.Actor, m.Qty, m.Note ?? "" }),
            [20, 10, 20, 14, 8, 30]);

        // 4. 검사 지연 LOT (알림 목록)
        Table(wb.AddWorksheet("검사 지연"), ["LOT", "품목", "검사 의뢰(한국)", "경과 시간", "미검사", "알림 기록(한국)"],
            delayed.Select(d => new XLCellValue[] { d.LotNo, $"{d.ItemCode} {d.ItemName}", Kst.FormatSeconds(d.RequestedAt), d.Hours, d.Uninspected,
                alerts.TryGetValue(d.LotId, out var at) ? Kst.FormatSeconds(at) : "" }),
            [20, 24, 20, 10, 8, 20]);

        // 5. 재고 대사 (정상 제외)
        Table(wb.AddWorksheet("재고 대사"), ["LOT", "결과", "생산", "생산 등식 차이", "R9 초과 보류", "원장 합 일치"],
            recon.Select(r => new XLCellValue[] { r.LotNo, r.State == "Mismatch" ? "불일치" : "초과 보류", r.ProducedQty, r.ProductionDiff, r.OverageQty, r.LedgerMatches ? "예" : "아니오" }),
            [20, 10, 8, 14, 12, 12]);

        return await storage.SaveAsync(businessDate, wb, ct);
    }

    private static void Table(IXLWorksheet ws, string[] header, IEnumerable<XLCellValue[]> rows, int[] widths)
    {
        for (var c = 0; c < header.Length; c++)
        {
            ws.Cell(1, c + 1).Value = header[c];
            ws.Column(c + 1).Width = widths[c];
        }
        var head = ws.Range(1, 1, 1, header.Length);
        head.Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightGray);
        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Length; c++) ws.Cell(r, c + 1).Value = row[c];
            r++;
        }
        if (r == 2) ws.Cell(2, 1).Value = "해당 없음";
        ws.SheetView.FreezeRows(1);
    }

    private static string VerdictLabel(string v) => v switch { Verdict.Pass => "합격", Verdict.Fail => "불합격", Verdict.Hold => "보류", _ => v };
}

/// <summary>보고서 파일 저장소. 파일 이름은 정해진 형식만 허용한다(경로 조작 차단).</summary>
public sealed partial class ReportStorage
{
    public sealed record ReportFile(string Name, DateOnly BusinessDate, long Size, DateTime WrittenAtUtc);

    public string Directory { get; }

    public ReportStorage(IConfiguration configuration, IWebHostEnvironment env)
    {
        var path = configuration["Reports:Path"];
        Directory = string.IsNullOrWhiteSpace(path) ? Path.Combine(env.ContentRootPath, "reports") : path;
        System.IO.Directory.CreateDirectory(Directory);
    }

    public static string FileName(DateOnly date) => $"qc-daily-{date:yyyyMMdd}.xlsx";

    [GeneratedRegex(@"^qc-daily-(\d{8})\.xlsx$")]
    private static partial Regex NamePattern();

    public bool Exists(DateOnly date) => File.Exists(Path.Combine(Directory, FileName(date)));

    public async Task<ReportFile> SaveAsync(DateOnly date, XLWorkbook workbook, CancellationToken ct)
    {
        var target = Path.Combine(Directory, FileName(date));
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                workbook.SaveAs(stream);
                await stream.FlushAsync(ct);
            }
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return Describe(new FileInfo(target))!;
    }

    public List<ReportFile> List() =>
        new DirectoryInfo(Directory).EnumerateFiles("qc-daily-*.xlsx")
            .Select(Describe).OfType<ReportFile>()
            .OrderByDescending(f => f.BusinessDate).ToList();

    /// <summary>내려받기용. 형식에 맞는 이름이고 실제로 있을 때만 경로를 돌려준다.</summary>
    public string? Resolve(string name)
    {
        if (!NamePattern().IsMatch(name)) return null;
        var path = Path.Combine(Directory, name);
        return File.Exists(path) ? path : null;
    }

    private static ReportFile? Describe(FileInfo f)
    {
        var m = NamePattern().Match(f.Name);
        if (!m.Success || !DateOnly.TryParseExact(m.Groups[1].Value, "yyyyMMdd", out var d)) return null;
        return new ReportFile(f.Name, d, f.Length, f.LastWriteTimeUtc);
    }
}
