using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Auth;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;

namespace QcTrace.Web.Api;

public static class LotEndpoints
{
    public static IEndpointRouteBuilder MapLotEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/lots").RequireAuthorization(Policies.AnyRole);

        // LOT 목록과 수량 지표 (조회 전용). status로 필터할 수 있다. 예: /api/lots?status=Waiting
        group.MapGet("/", async (string? status, QcTraceDbContext db) =>
        {
            var query = db.Lots.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(status))
            {
                var matched = LotStatus.All.FirstOrDefault(s => s.Equals(status, StringComparison.OrdinalIgnoreCase));
                if (matched is null)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "알 수 없는 LOT 상태입니다.",
                        detail: $"status는 {string.Join(", ", LotStatus.All)} 중 하나여야 합니다.");
                }
                query = query.Where(l => l.Status == matched);
            }

            var lots = await query
                .OrderByDescending(l => l.CreatedAt)
                .Select(l => new
                {
                    l.Id,
                    l.LotNo,
                    ItemCode = l.Item.ItemCode,
                    ItemName = l.Item.Name,
                    l.ProducedQty,
                    l.ProducedOn,
                    l.Shift,
                    l.Status,
                    l.CreatedAt,
                    l.InspectionRequestedAt,
                    Quantity = l.Quantity == null ? null : new
                    {
                        l.Quantity.Uninspected,
                        l.Quantity.Passed,
                        l.Quantity.Failed,
                        l.Quantity.Held,
                        l.Quantity.Shipped,
                        l.Quantity.Reserved,
                        l.Quantity.Scrapped,
                        Available = l.Quantity.Passed - l.Quantity.Shipped - l.Quantity.Reserved
                    }
                })
                .ToListAsync();

            return Results.Ok(lots);
        });

        return app;
    }
}
