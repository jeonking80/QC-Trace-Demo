using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;
using QcTrace.Web.Services;

namespace QcTrace.Web.Pages.Schedule;

/// <summary>
/// 일정 결정(관리자·승인권자): 생산 담당이 검사자 일정 회신을 거절한 LOT의 최종 기한을 정한다(usp_DecideSchedule).
/// 기한이 지난 LOT도 함께 보여 준다(보고용, 결정 대상은 거절된 것만).
/// </summary>
public class IndexModel(QcTraceDbContext db, QcCommands qc) : QcPageModel
{
    public sealed record Pending(Lot Lot, List<Services.Schedule.HistoryRow> History);

    public List<Pending> ToDecide { get; private set; } = [];
    public List<Lot> Overdue { get; private set; } = [];
    public List<(string LotNo, Services.Schedule.HistoryRow Row)> Recent { get; private set; } = [];

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostDecideAsync(int lotId, string requestKey, DateTime? dueAt, string? reason)
    {
        if (dueAt is null)
        {
            ModelState.AddModelError("", "최종 기한을 입력하세요.");
        }
        else
        {
            var result = await TryRunAsync(() => qc.DecideScheduleAsync(requestKey, lotId, CurrentUserId, Kst.ToUtc(dueAt.Value), reason));
            if (result is not null)
            {
                StatusMessage = $"최종 기한을 {Kst.Format(Kst.ToUtc(dueAt.Value))}로 정했습니다. 검사자 목록과 지연 판단이 이 기한을 따릅니다.";
                return RedirectToPage();
            }
        }
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var lots = db.Lots.AsNoTracking().Include(l => l.Item).Include(l => l.Quantity);
        ToDecide = [];
        foreach (var lot in await lots.Where(l => l.ScheduleState == ScheduleState.Escalated).OrderBy(l => l.InspectionRequestedAt).ToListAsync())
        {
            ToDecide.Add(new Pending(lot, await Services.Schedule.HistoryAsync(db, lot.Id)));
        }
        var now = DateTime.UtcNow;
        Overdue = await lots.Where(l => (l.Status == LotStatus.Waiting || l.Status == LotStatus.Inspecting)
                                        && l.Quantity!.Uninspected > 0 && l.DueAt != null && l.DueAt < now)
            .OrderBy(l => l.DueAt).ToListAsync();
        Recent = await (
            from e in db.ScheduleEvents.AsNoTracking()
            join l in db.Lots on e.LotId equals l.Id
            join u in db.Users on e.ActorId equals u.Id
            orderby e.CreatedAt descending, e.Id descending
            select new { l.LotNo, Row = new Services.Schedule.HistoryRow(e.CreatedAt, e.Kind, e.Priority, e.DueAt, e.Reason, u.DisplayName + " (" + u.UserName + ")") })
            .Take(30).Select(x => ValueTuple.Create(x.LotNo, x.Row)).ToListAsync();
    }
}
