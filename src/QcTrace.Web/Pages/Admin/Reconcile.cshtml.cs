using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Services;

namespace QcTrace.Web.Pages.Admin;

/// <summary>
/// 재고 대사. 판정은 DB 뷰 vw_LotReconciliation이 한다(앱이 수량을 다시 계산하지 않는다).
/// - 불일치: LotQuantities ≠ 원장 합, 출하+예약 > 합격, 또는 생산 등식 차이가 R9 초과 보류와 다름
/// - 초과 보류: 생산 등식이 R9 초과분만큼만 어긋남(정상이지만 확인 대상, PLAN 3장)
/// </summary>
public class ReconcileModel(QcTraceDbContext db) : QcPageModel
{
    public const string Mismatch = "Mismatch";
    public const string OverageHeld = "OverageHeld";
    public const string Ok = "OK";

    /// <summary>problems(기본): 불일치·초과 보류만, all: 전체</summary>
    [BindProperty(SupportsGet = true)] public string? Show { get; set; }

    public sealed class Row
    {
        public int LotId { get; set; }
        public string LotNo { get; set; } = "";
        public string Status { get; set; } = "";
        public int ProducedQty { get; set; }
        public int Uninspected { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        public int Held { get; set; }
        public int Scrapped { get; set; }
        public int Shipped { get; set; }
        public int Reserved { get; set; }
        public int LedgerUninspected { get; set; }
        public int LedgerPassed { get; set; }
        public int LedgerFailed { get; set; }
        public int LedgerHeld { get; set; }
        public int LedgerScrapped { get; set; }
        public int LedgerShipped { get; set; }
        public int LedgerReserved { get; set; }
        public int OverageQty { get; set; }
        public int ProductionDiff { get; set; }
        public bool LedgerMatches { get; set; }
        public bool ShipmentWithinPassed { get; set; }
        public string State { get; set; } = "";
    }

    public List<Row> Rows { get; private set; } = [];
    public int MismatchCount { get; private set; }
    public int OverageCount { get; private set; }
    public int OkCount { get; private set; }
    public DateTime CheckedAt { get; private set; }
    public bool ShowAll => Show == "all";

    public async Task OnGetAsync()
    {
        var all = await LoadAsync();
        CheckedAt = DateTime.UtcNow;
        MismatchCount = all.Count(r => r.State == Mismatch);
        OverageCount = all.Count(r => r.State == OverageHeld);
        OkCount = all.Count(r => r.State == Ok);
        Rows = ShowAll ? all : all.Where(r => r.State != Ok).ToList();
    }

    public async Task<IActionResult> OnGetCsvAsync()
    {
        var rows = await LoadAsync();
        var csv = Csv.Write(
            ["LOT", "상태", "생산", "미검사", "합격", "불합격", "보류", "폐기", "출하", "예약",
             "원장 미검사", "원장 합격", "원장 불합격", "원장 보류", "원장 폐기", "원장 출하", "원장 예약",
             "생산 등식 차이", "R9 초과 보류", "대사 결과"],
            rows.Select(r => new object?[] { r.LotNo, r.Status, r.ProducedQty, r.Uninspected, r.Passed, r.Failed, r.Held, r.Scrapped, r.Shipped, r.Reserved,
                r.LedgerUninspected, r.LedgerPassed, r.LedgerFailed, r.LedgerHeld, r.LedgerScrapped, r.LedgerShipped, r.LedgerReserved,
                r.ProductionDiff, r.OverageQty, StateLabel(r.State) }));
        return File(csv, "text/csv; charset=utf-8", $"reconciliation-{Kst.FromUtc(DateTime.UtcNow):yyyyMMdd-HHmm}.csv");
    }

    public static string StateLabel(string state) => state switch
    {
        Mismatch => "불일치",
        OverageHeld => "초과 보류",
        _ => "정상"
    };

    private Task<List<Row>> LoadAsync() =>
        db.Database.SqlQuery<Row>($"""
            SELECT LotId, LotNo, Status, ProducedQty, Uninspected, Passed, Failed, Held, Scrapped, Shipped, Reserved,
                   LedgerUninspected, LedgerPassed, LedgerFailed, LedgerHeld, LedgerScrapped, LedgerShipped, LedgerReserved,
                   OverageQty, ProductionDiff, LedgerMatches, ShipmentWithinPassed, State
            FROM dbo.vw_LotReconciliation
            """)
            .OrderBy(r => r.State == Mismatch ? 0 : r.State == OverageHeld ? 1 : 2).ThenBy(r => r.LotNo)
            .ToListAsync();
}
