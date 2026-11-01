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

    public List<Lot> Lots { get; private set; } = [];
    /// <summary>LOT별 가장 최근 불합격·보류 판정 사유(생산 담당도 이유를 알 수 있게, 집 PC 피드백 B2)</summary>
    public Dictionary<int, (string Verdict, string? Reason, string Inspector)> LatestIssue { get; private set; } = new();
    public List<SelectListItem> ItemOptions { get; private set; } = [];

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
                return RedirectToPage(new { Status });
            }
        }
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRequestInspectionAsync(int lotId, string requestKey)
    {
        var result = await TryRunAsync(() => qc.RequestInspectionAsync(requestKey, lotId, CurrentUserId));
        if (result is not null)
        {
            StatusMessage = "검사를 의뢰했습니다.";
            return RedirectToPage(new { Status });
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
        Lots = await query.OrderByDescending(l => l.CreatedAt).Take(200).ToListAsync();
        var ids = Lots.Select(l => l.Id).ToList();
        var issues = await (
            from i in db.Inspections.AsNoTracking()
            join u in db.Users on i.InspectorId equals u.Id
            where ids.Contains(i.LotId) && i.Verdict != Verdict.Pass
            orderby i.CreatedAt descending
            select new { i.LotId, i.Verdict, i.Reason, u.DisplayName }).ToListAsync();
        LatestIssue = issues.GroupBy(x => x.LotId).ToDictionary(g => g.Key, g => (g.First().Verdict, (string?)g.First().Reason, g.First().DisplayName));
        ItemOptions = await db.Items.AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.ItemCode)
            .Select(i => new SelectListItem($"{i.ItemCode} {i.Name}", i.Id.ToString()))
            .ToListAsync();
    }
}
