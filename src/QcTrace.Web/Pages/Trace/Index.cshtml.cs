using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;

namespace QcTrace.Web.Pages.Trace;

/// <summary>LOT 조회(역할이 있는 사용자 누구나). LOT 번호 일부나 상태로 찾고, 눌러서 상세로 간다.</summary>
public class IndexModel(QcTraceDbContext db) : QcPageModel
{
    public const int Limit = 100;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }

    /// <summary>대시보드 카드에서 넘어온 목록: waiting(검사 대기), delayed(검사 지연), failed(불합격 처리 대기), shipped-today(오늘 출하)</summary>
    [BindProperty(SupportsGet = true)] public string? Need { get; set; }

    public static readonly IReadOnlyDictionary<string, string> Needs = new Dictionary<string, string>
    {
        ["waiting"] = "검사 대기 (미검사가 남은 의뢰 LOT)",
        ["delayed"] = $"검사 지연 (검사 기한을 넘겼거나, 기한 없이 의뢰 후 {(int)QcTrace.Web.Services.QcQueries.DelayThreshold.TotalHours}시간 넘게 미검사가 남은 LOT)",
        ["failed"] = "불합격 처리 대기 (재검사·폐기 전 불합격이 남은 LOT)",
        ["shipped-today"] = "오늘 출하한 LOT",
    };

    public string? NeedLabel => Need is not null && Needs.TryGetValue(Need, out var label) ? label : null;
    public List<Lot> Lots { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var query = db.Lots.AsNoTracking().Include(l => l.Item).Include(l => l.Quantity).AsQueryable();
        if (!string.IsNullOrWhiteSpace(Q)) query = query.Where(l => l.LotNo.Contains(Q.Trim()) || l.Item.ItemCode.Contains(Q.Trim()));
        if (!string.IsNullOrEmpty(Status)) query = query.Where(l => l.Status == Status);
        var oldestFirst = false;
        switch (Need)
        {
            case "waiting":
                query = query.Where(l => (l.Status == LotStatus.Waiting || l.Status == LotStatus.Inspecting) && l.Quantity!.Uninspected > 0);
                oldestFirst = true;
                break;
            case "delayed":
                // 대시보드 카드·지연 알림과 같은 정의(기한이 있으면 기한, 없으면 48시간)를 한곳(QcQueries)에서 가져온다
                var delayedIds = (await QcTrace.Web.Services.QcQueries.DelayedLotsAsync(db, DateTime.UtcNow)).Select(d => d.LotId).ToList();
                query = query.Where(l => delayedIds.Contains(l.Id));
                oldestFirst = true;
                break;
            case "failed":
                query = query.Where(l => l.Quantity!.Failed > 0);
                break;
            case "shipped-today":
                var since = QcTrace.Web.Services.Kst.StartOfDayUtc(QcTrace.Web.Services.Kst.Today);
                query = query.Where(l => db.Shipments.Any(s => s.LotId == l.Id && s.CreatedAt >= since));
                break;
        }
        query = oldestFirst ? query.OrderBy(l => l.InspectionRequestedAt) : query.OrderByDescending(l => l.CreatedAt);
        Lots = await query.Take(Limit).ToListAsync();
    }
}
