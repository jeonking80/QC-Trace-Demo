using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace QcTrace.Web.Services;

/// <summary>SP 응답. Json은 SP가 만든 ResponseJson, Replayed는 같은 요청 키 재전송이었는지.</summary>
public sealed record CommandResult(JsonElement Json, bool Replayed);

/// <summary>
/// 수량을 바꾸는 모든 작업의 단일 통로. 화면과 API가 같은 SP를 같은 인자로 부른다.
/// 업무 규칙(R1~R10)은 SP가 검사하고, 위반은 SqlException(THROW 번호)으로 올라온다 → SqlErrors.Classify.
/// </summary>
public sealed class QcCommands(IConfiguration configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("QcTrace")
        ?? throw new InvalidOperationException("ConnectionStrings:QcTrace 설정이 없습니다.");

    public Task<CommandResult> RegisterLotAsync(string requestKey, string producerId, int itemId, int producedQty, DateOnly producedOn, string shift) =>
        ExecAsync("dbo.usp_RegisterLot",
            ("@RequestKey", requestKey), ("@ProducerId", producerId), ("@ItemId", itemId), ("@ProducedQty", producedQty),
            ("@ProducedOn", producedOn.ToDateTime(TimeOnly.MinValue)), ("@Shift", shift));

    public Task<CommandResult> RequestInspectionAsync(string requestKey, int lotId, string producerId,
        string priority = "Normal", string? priorityReason = null, DateTime? dueAtUtc = null) =>
        ExecAsync("dbo.usp_RequestInspection", ("@RequestKey", requestKey), ("@LotId", lotId), ("@ProducerId", producerId),
            ("@Priority", priority), ("@PriorityReason", priorityReason), ("@DueAt", dueAtUtc));

    /// <summary>검사자: 남은 미검사의 완료 예정 시각(UTC)과 사유 회신</summary>
    public Task<CommandResult> ProposeScheduleAsync(string requestKey, int lotId, string inspectorId, DateTime dueAtUtc, string? reason) =>
        ExecAsync("dbo.usp_ProposeSchedule", ("@RequestKey", requestKey), ("@LotId", lotId), ("@InspectorId", inspectorId),
            ("@DueAt", dueAtUtc), ("@Reason", reason));

    /// <summary>생산 담당: 회신 수락(Accept) 또는 거절(Reject, 사유 필수 → 결정권자에게)</summary>
    public Task<CommandResult> RespondScheduleAsync(string requestKey, int lotId, string producerId, string decision, string? reason) =>
        ExecAsync("dbo.usp_RespondSchedule", ("@RequestKey", requestKey), ("@LotId", lotId), ("@ProducerId", producerId),
            ("@Decision", decision), ("@Reason", reason));

    /// <summary>결정권자(관리자·승인권자): 거절된 일정의 최종 기한(UTC)</summary>
    public Task<CommandResult> DecideScheduleAsync(string requestKey, int lotId, string deciderId, DateTime dueAtUtc, string? reason) =>
        ExecAsync("dbo.usp_DecideSchedule", ("@RequestKey", requestKey), ("@LotId", lotId), ("@DeciderId", deciderId),
            ("@DueAt", dueAtUtc), ("@Reason", reason));

    public Task<CommandResult> ApplyInspectionAsync(string requestKey, int lotId, string inspectorId, int qty, string verdict,
        string? measuredJson, string? reason, long? correctsInspectionId = null, int? defectCodeId = null) =>
        ExecAsync("dbo.usp_ApplyInspection",
            ("@RequestKey", requestKey), ("@LotId", lotId), ("@InspectorId", inspectorId), ("@Qty", qty), ("@Verdict", verdict),
            ("@MeasuredJson", measuredJson), ("@Reason", reason), ("@CorrectsInspectionId", correctsInspectionId),
            ("@DefectCodeId", defectCodeId));

    public Task<CommandResult> DisposeFailedAsync(string requestKey, int lotId, string inspectorId, int qty, string action, string? reason) =>
        ExecAsync("dbo.usp_DisposeFailed",
            ("@RequestKey", requestKey), ("@LotId", lotId), ("@InspectorId", inspectorId), ("@Qty", qty),
            ("@Action", action), ("@Reason", reason));

    public Task<CommandResult> ApproveShipmentAsync(string requestKey, int lotId, string approverId, int qty, string decision, string? reason) =>
        ExecAsync("dbo.usp_ApproveShipment",
            ("@RequestKey", requestKey), ("@LotId", lotId), ("@ApproverId", approverId), ("@Qty", qty),
            ("@Decision", decision), ("@Reason", reason));

    public Task<CommandResult> ShipLotAsync(string requestKey, int lotId, string shipperId, int qty, string? destination) =>
        ExecAsync("dbo.usp_ShipLot",
            ("@RequestKey", requestKey), ("@LotId", lotId), ("@ShipperId", shipperId), ("@Qty", qty), ("@Destination", destination));

    /// <summary>역할 변경(관리자). roles는 바뀐 뒤의 전체 역할 목록. 바뀐 역할마다 RoleChanges에 기록된다.</summary>
    public Task<CommandResult> SetUserRolesAsync(string actorId, string userId, IEnumerable<string> roles, string? reason) =>
        ExecAsync("dbo.usp_SetUserRoles",
            ("@ActorId", actorId), ("@UserId", userId), ("@Roles", string.Join(",", roles)), ("@Reason", reason));

    private async Task<CommandResult> ExecAsync(string procedure, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(procedure, connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"{procedure}가 결과 행을 돌려주지 않았습니다.");
        }
        var json = JsonDocument.Parse(reader.GetString(0)).RootElement.Clone();
        return new CommandResult(json, reader.GetBoolean(1));
    }
}
