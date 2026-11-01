using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using QcTrace.Web.Auth;
using QcTrace.Web.Services;

namespace QcTrace.Web.Api;

/// <summary>
/// 수량을 바꾸는 API. 화면과 같은 SP(QcCommands)를 부르므로 화면을 거치지 않아도 같은 규칙이 적용된다.
/// 모든 요청에 Idempotency-Key 헤더가 필요하다(R6). 같은 키 재전송이면 응답 헤더 Idempotent-Replayed: true.
/// 사용자 지정 헤더가 필수라 다른 사이트의 폼·스크립트가 쿠키만으로 호출할 수 없다(CORS 사전 요청에서 막힘).
/// </summary>
public static class CommandEndpoints
{
    public const string IdempotencyHeader = "Idempotency-Key";

    public sealed record RegisterLotRequest(int ItemId, int ProducedQty, DateOnly ProducedOn, string? Shift);
    public sealed record InspectionRequest(int Qty, string? Verdict, string? MeasuredJson, string? Reason, long? CorrectsInspectionId, int? DefectCodeId = null);
    public sealed record DispositionRequest(int Qty, string? Action, string? Reason);
    public sealed record ApprovalRequest(int Qty, string? Decision, string? Reason);
    public sealed record ShipmentRequest(int Qty, string? Destination);

    public static IEndpointRouteBuilder MapCommandEndpoints(this IEndpointRouteBuilder app)
    {
        var lots = app.MapGroup("/api/lots");

        lots.MapPost("/", (RegisterLotRequest body, HttpContext http, QcCommands qc) =>
            Run(http, (key, user) => qc.RegisterLotAsync(key, user, body.ItemId, body.ProducedQty, body.ProducedOn, body.Shift ?? "")))
            .RequireAuthorization(Policies.Producer);

        lots.MapPost("/{lotId:int}/inspection-request", (int lotId, HttpContext http, QcCommands qc) =>
            Run(http, (key, user) => qc.RequestInspectionAsync(key, lotId, user)))
            .RequireAuthorization(Policies.Producer);

        lots.MapPost("/{lotId:int}/inspections", (int lotId, InspectionRequest body, HttpContext http, QcCommands qc) =>
            Run(http, (key, user) => qc.ApplyInspectionAsync(key, lotId, user, body.Qty, body.Verdict ?? "",
                body.MeasuredJson, body.Reason, body.CorrectsInspectionId, body.DefectCodeId)))
            .RequireAuthorization(Policies.Inspector);

        lots.MapPost("/{lotId:int}/dispositions", (int lotId, DispositionRequest body, HttpContext http, QcCommands qc) =>
            Run(http, (key, user) => qc.DisposeFailedAsync(key, lotId, user, body.Qty, body.Action ?? "", body.Reason)))
            .RequireAuthorization(Policies.Disposer);   // 재검사는 검사자, 폐기는 관리자(SP가 처리 방법별로 역할 검사)

        lots.MapPost("/{lotId:int}/approvals", (int lotId, ApprovalRequest body, HttpContext http, QcCommands qc) =>
            Run(http, (key, user) => qc.ApproveShipmentAsync(key, lotId, user, body.Qty, body.Decision ?? "Approved", body.Reason)))
            .RequireAuthorization(Policies.Approver);

        lots.MapPost("/{lotId:int}/shipments", (int lotId, ShipmentRequest body, HttpContext http, QcCommands qc) =>
            Run(http, (key, user) => qc.ShipLotAsync(key, lotId, user, body.Qty, body.Destination)))
            .RequireAuthorization(Policies.Shipper);

        return app;
    }

    private static async Task<IResult> Run(HttpContext http, Func<string, string, Task<CommandResult>> command)
    {
        var key = http.Request.Headers[IdempotencyHeader].ToString().Trim();
        if (key.Length is 0 or > 100)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Idempotency-Key 헤더가 필요합니다.",
                detail: "같은 작업을 다시 보낼 때 같은 값을 쓰는 1~100자 키를 보내세요. (R6)",
                extensions: new Dictionary<string, object?> { ["errorCode"] = "idempotency_key_required" });
        }

        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var result = await command(key, userId);
        http.Response.Headers["Idempotent-Replayed"] = result.Replayed ? "true" : "false";
        return Results.Text(result.Json.GetRawText(), "application/json; charset=utf-8");
    }
}
