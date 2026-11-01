using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;
using QcTrace.Web.Services;

namespace QcTrace.Web.Pages.Lots;

public class IndexModel(QcTraceDbContext db, QcCommands qc) : QcPageModel
{
    public static readonly string[] Shifts = ["A조", "B조", "C조"];

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    /// <summary>LOT 번호 또는 품목 코드 일부(LOT 조회와 같은 검색, 생산 담당 메뉴 통합)</summary>
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public List<Lot> Lots { get; private set; } = [];
    /// <summary>LOT별 가장 최근 불합격·보류 판정 사유(생산 담당도 이유를 알 수 있게, 집 PC 피드백 B2)</summary>
    public Dictionary<int, (string Verdict, string? Reason, string Inspector)> LatestIssue { get; private set; } = new();
    public List<SelectListItem> ItemOptions { get; private set; } = [];
    /// <summary>검사자가 보낸 일정 회신 중 확인을 기다리는 것(검사 일정, 2026-11-01 사용자 요청)</summary>
    public List<(Lot Lot, Services.Schedule.HistoryRow Proposal)> PendingProposals { get; private set; } = [];

    [BindProperty]
    public RegisterInput Input { get; set; } = new();

    public sealed class RegisterInput
    {
        public string RequestKey { get; set; } = NewRequestKey();

        [Required(ErrorMessage = "품목을 고르세요.")]
        public int? ItemId { get; set; }

        [Required(ErrorMessage = "생산 수량을 입력하세요."), Range(1, 1_000_000, ErrorMessage = "생산 수량은 1 이상이어야 합니다.")]
        public int? ProducedQty { get; set; }

        [Required(ErrorMessage = "생산일을 입력하세요.")]
        public DateOnly? ProducedOn { get; set; } = Kst.Today;

        [Required(ErrorMessage = "작업반을 고르세요.")]
        public string? Shift { get; set; } = Shifts[0];
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostRegisterAsync()
    {
        if (ModelState.IsValid)
        {
            var result = await TryRunAsync(() => qc.RegisterLotAsync(Input.RequestKey, CurrentUserId,
                Input.ItemId!.Value, Input.ProducedQty!.Value, Input.ProducedOn!.Value, Input.Shift!));
            if (result is not null)
            {
                StatusMessage = $"LOT {result.Json.GetProperty("lotNo").GetString()}을 등록했습니다. 검사 의뢰를 눌러야 검사자가 검사할 수 있습니다.";
                return RedirectToPage(new { Status, Q });
            }
        }
        await LoadAsync();
        return Page();
    }

    /// <summary>검사 의뢰. 긴급(사유 필수)·희망 완료 기한(한국 시각)은 선택.</summary>
    public async Task<IActionResult> OnPostRequestInspectionAsync(int lotId, string requestKey,
        string? priority, string? priorityReason, DateTime? dueAt)
    {
        var urgent = priority == LotPriority.Urgent;
        var result = await TryRunAsync(() => qc.RequestInspectionAsync(requestKey, lotId, CurrentUserId,
            urgent ? LotPriority.Urgent : LotPriority.Normal, urgent ? priorityReason : null, Services.Schedule.KstInputToUtc(dueAt)));
        if (result is not null)
        {
            StatusMessage = urgent ? "검사를 긴급으로 의뢰했습니다." : "검사를 의뢰했습니다.";
            return RedirectToPage(new { Status, Q });
        }
        await LoadAsync();
        return Page();
    }

    /// <summary>검사자 일정 회신 확인: 수락하면 기한 확정, 거절(사유 필수)하면 결정권자에게 넘어간다.</summary>
    public async Task<IActionResult> OnPostRespondScheduleAsync(int lotId, string requestKey, string decision, string? reason)
    {
        var result = await TryRunAsync(() => qc.RespondScheduleAsync(requestKey, lotId, CurrentUserId, decision, reason));
        if (result is not null)
        {
            var state = result.Json.TryGetProperty("scheduleState", out var st) ? st.GetString() : null;
            StatusMessage = decision == "Accept" && state == ScheduleState.Escalated
                ? "검사자 일정을 수락했습니다. 이 LOT은 결정권자가 기한을 정했으므로, 바꾸려면 결정권자(관리자·승인권자)가 다시 확인합니다."
                : decision == "Accept"
                ? "검사자 일정을 수락했습니다. 그 시각이 검사 기한이 됩니다."
                : "검사자 일정을 거절했습니다. 결정권자(관리자·승인권자)가 최종 기한을 정합니다.";
            return RedirectToPage(new { Status, Q });
        }
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var query = db.Lots.AsNoTracking().Include(l => l.Item).Include(l => l.Quantity).AsQueryable();
        if (!string.IsNullOrEmpty(Status) && LotStatus.All.Contains(Status))
        {
            query = query.Where(l => l.Status == Status);
        }
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim();
            query = query.Where(l => l.LotNo.Contains(q) || l.Item.ItemCode.Contains(q));
        }
        Lots = await query.OrderByDescending(l => l.CreatedAt).Take(200).ToListAsync();
        var ids = Lots.Select(l => l.Id).ToList();
        var issues = await (
            from i in db.Inspections.AsNoTracking()
            join u in db.Users on i.InspectorId equals u.Id
            where ids.Contains(i.LotId) && i.Verdict != Verdict.Pass
            orderby i.CreatedAt descending
            select new { i.LotId, i.Verdict, i.Reason, u.DisplayName }).ToListAsync();
        LatestIssue = issues.GroupBy(x => x.LotId).ToDictionary(g => g.Key, g => (g.First().Verdict, (string?)g.First().Reason, g.First().DisplayName));
        var proposed = await db.Lots.AsNoTracking().Include(l => l.Item).Include(l => l.Quantity)
            .Where(l => l.ScheduleState == ScheduleState.Proposed).OrderBy(l => l.ProposedDueAt).ToListAsync();
        PendingProposals = [];
        foreach (var lot in proposed)
        {
            var last = (await Services.Schedule.HistoryAsync(db, lot.Id)).LastOrDefault(h => h.Kind == ScheduleEventKind.Propose);
            if (last is not null) PendingProposals.Add((lot, last));
        }
        ItemOptions = await db.Items.AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.ItemCode)
            .Select(i => new SelectListItem($"{i.ItemCode} {i.Name}", i.Id.ToString()))
            .ToListAsync();
    }
}
