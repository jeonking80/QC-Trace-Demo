using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Services;

namespace QcTrace.Web.Pages.Admin;

/// <summary>
/// 일일 보고서 파일과 검사 지연 알림. 보고서는 QcAutomation이 매일 자동으로 만들고, 여기서 날짜를 골라 다시 만들 수도 있다.
/// </summary>
public class ReportsModel(QcTraceDbContext db, ReportStorage storage, DailyReport report, QcAutomation automation, TimeProvider time) : QcPageModel
{
    public List<ReportStorage.ReportFile> Files { get; private set; } = [];
    public List<AlertRow> OpenAlerts { get; private set; } = [];
    public List<AlertRow> ClosedAlerts { get; private set; } = [];
    public DateOnly DefaultDate { get; private set; }
    public TimeOnly RunAt => automation.RunAt;

    [BindProperty] public DateOnly? BusinessDate { get; set; }

    public sealed record AlertRow(string LotNo, string ItemCode, DateTime RequestedAt, DateTime DetectedAt, int UninspectedAtDetection,
        int Uninspected, string Status, int Hours);

    public async Task OnGetAsync()
    {
        var now = time.GetUtcNow().UtcDateTime;
        DefaultDate = Kst.Today.AddDays(-1);
        Files = storage.List();
        var delayedIds = (await QcQueries.DelayedLotsAsync(db, now)).Select(d => d.LotId).ToHashSet();
        var alerts = await (
            from a in db.DelayAlerts.AsNoTracking()
            join l in db.Lots on a.LotId equals l.Id
            orderby a.DetectedAt descending
            select new { a.LotId, l.LotNo, l.Item.ItemCode, a.InspectionRequestedAt, a.DetectedAt, a.UninspectedAtDetection,
                Uninspected = l.Quantity!.Uninspected, l.Status })
            .Take(200)
            .ToListAsync();
        var rows = alerts.Select(a => (Open: delayedIds.Contains(a.LotId), Row: new AlertRow(a.LotNo, a.ItemCode, a.InspectionRequestedAt, a.DetectedAt,
            a.UninspectedAtDetection, a.Uninspected, a.Status, (int)(now - a.InspectionRequestedAt).TotalHours))).ToList();
        OpenAlerts = rows.Where(r => r.Open).Select(r => r.Row).OrderBy(r => r.RequestedAt).ToList();
        ClosedAlerts = rows.Where(r => !r.Open).Select(r => r.Row).Take(50).ToList();
    }

    public async Task<IActionResult> OnPostGenerateAsync()
    {
        var today = Kst.Today;
        if (BusinessDate is not { } date || date >= today || date < today.AddYears(-1))
        {
            ModelState.AddModelError("", "보고 기준일은 어제 이전, 1년 안의 날짜로 고르세요.");
            await OnGetAsync();
            return Page();
        }
        var file = await report.GenerateAsync(date);
        StatusMessage = $"{file.Name}을 만들었습니다.";
        return RedirectToPage();
    }

    public IActionResult OnGetDownload(string name)
    {
        var path = storage.Resolve(name);
        if (path is null) return NotFound();
        return PhysicalFile(path, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
    }
}
