using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;
using QcTrace.Web.Services;

namespace QcTrace.Web.Pages.Inspections;

/// <summary>
/// 검사자 일정 회신: 남은 미검사를 언제까지 하겠다는 예정 시각과 사유를 보낸다(usp_ProposeSchedule).
/// 생산 담당이 수락하면 그 시각이 기한이 되고, 거절하면 결정권자(관리자·승인권자)가 최종 기한을 정한다.
/// </summary>
public class ScheduleModel(QcTraceDbContext db, QcCommands qc) : QcPageModel
{
    public Lot Lot { get; private set; } = null!;
    public List<Services.Schedule.HistoryRow> History { get; private set; } = [];

    [BindProperty] public string RequestKey { get; set; } = NewRequestKey();
    [BindProperty] public DateTime? DueAt { get; set; }
    [BindProperty] public string? Reason { get; set; }

    public bool CanPropose => Lot.Quantity is { Uninspected: > 0 }
        && Lot.Status is LotStatus.Waiting or LotStatus.Inspecting
        && Lot.ScheduleState is not (ScheduleState.Proposed or ScheduleState.Escalated);

    public async Task<IActionResult> OnGetAsync(int lotId)
    {
        if (!await LoadAsync(lotId)) return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int lotId)
    {
        if (!await LoadAsync(lotId)) return NotFound();
        if (DueAt is null)
        {
            ModelState.AddModelError("", "완료 예정 시각을 입력하세요.");
            return Page();
        }
        var result = await TryRunAsync(() => qc.ProposeScheduleAsync(RequestKey, lotId, CurrentUserId, Kst.ToUtc(DueAt.Value), Reason));
        if (result is null) return Page();
        StatusMessage = $"{Lot.LotNo} 남은 미검사 {Lot.Quantity!.Uninspected}개를 {Kst.Format(Kst.ToUtc(DueAt.Value))}까지 하겠다고 회신했습니다. 생산 담당이 확인합니다.";
        return RedirectToPage("./Index");
    }

    private async Task<bool> LoadAsync(int lotId)
    {
        var lot = await db.Lots.AsNoTracking().Include(l => l.Item).Include(l => l.Quantity).FirstOrDefaultAsync(l => l.Id == lotId);
        if (lot is null) return false;
        Lot = lot;
        History = await Services.Schedule.HistoryAsync(db, lotId);
        return true;
    }
}
