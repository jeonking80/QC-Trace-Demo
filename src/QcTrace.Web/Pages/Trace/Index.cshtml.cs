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
    public List<Lot> Lots { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var query = db.Lots.AsNoTracking().Include(l => l.Item).Include(l => l.Quantity).AsQueryable();
        if (!string.IsNullOrWhiteSpace(Q)) query = query.Where(l => l.LotNo.Contains(Q.Trim()) || l.Item.ItemCode.Contains(Q.Trim()));
        if (!string.IsNullOrEmpty(Status)) query = query.Where(l => l.Status == Status);
        Lots = await query.OrderByDescending(l => l.CreatedAt).Take(Limit).ToListAsync();
    }
}
