using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace QcTrace.IntegrationTests.Infrastructure;

public sealed record Quantities(int Uninspected, int Passed, int Failed, int Held, int Shipped, int Reserved, int Scrapped = 0)
{
    public int Available => Passed - Shipped - Reserved;
}

public sealed record SpResult(JsonElement Response, bool Replayed);

/// <summary>SP 호출과 조회 도우미. SP는 앱 계정으로, 검증 조회는 관리자 계정으로 한다.</summary>
public sealed class Db(SqlServerFixture fixture)
{
    public string User(string userName) => fixture.UserIds[userName];

    public Task<SpResult> ApplyInspection(string key, int lotId, string inspector, int qty, string verdict,
        string? reason = null, long? correctsInspectionId = null, string? measuredJson = null, int? defectCodeId = null) =>
        defectCodeId is null
            ? CallAsync("dbo.usp_ApplyInspection",
                ("@RequestKey", key), ("@LotId", lotId), ("@InspectorId", User(inspector)), ("@Qty", qty),
                ("@Verdict", verdict), ("@MeasuredJson", measuredJson), ("@Reason", reason),
                ("@CorrectsInspectionId", correctsInspectionId))
            : CallAsync("dbo.usp_ApplyInspection",
                ("@RequestKey", key), ("@LotId", lotId), ("@InspectorId", User(inspector)), ("@Qty", qty),
                ("@Verdict", verdict), ("@MeasuredJson", measuredJson), ("@Reason", reason),
                ("@CorrectsInspectionId", correctsInspectionId), ("@DefectCodeId", defectCodeId));

    public Task<SpResult> RegisterLot(string key, string producer, int itemId, int qty, DateTime producedOn, string shift = "A조") =>
        CallAsync("dbo.usp_RegisterLot",
            ("@RequestKey", key), ("@ProducerId", User(producer)), ("@ItemId", itemId), ("@ProducedQty", qty),
            ("@ProducedOn", producedOn.Date), ("@Shift", shift));

    public Task<SpResult> RequestInspection(string key, int lotId, string producer) =>
        CallAsync("dbo.usp_RequestInspection", ("@RequestKey", key), ("@LotId", lotId), ("@ProducerId", User(producer)));

    public Task<SpResult> RequestInspection(string key, int lotId, string producer, string priority, string? priorityReason, DateTime? dueAtUtc) =>
        CallAsync("dbo.usp_RequestInspection", ("@RequestKey", key), ("@LotId", lotId), ("@ProducerId", User(producer)),
            ("@Priority", priority), ("@PriorityReason", priorityReason), ("@DueAt", dueAtUtc));

    public Task<SpResult> ProposeSchedule(string key, int lotId, string inspector, DateTime dueAtUtc, string? reason) =>
        CallAsync("dbo.usp_ProposeSchedule", ("@RequestKey", key), ("@LotId", lotId), ("@InspectorId", User(inspector)),
            ("@DueAt", dueAtUtc), ("@Reason", reason));

    public Task<SpResult> RespondSchedule(string key, int lotId, string producer, string decision, string? reason) =>
        CallAsync("dbo.usp_RespondSchedule", ("@RequestKey", key), ("@LotId", lotId), ("@ProducerId", User(producer)),
            ("@Decision", decision), ("@Reason", reason));

    public Task<SpResult> DecideSchedule(string key, int lotId, string decider, DateTime dueAtUtc, string? reason) =>
        CallAsync("dbo.usp_DecideSchedule", ("@RequestKey", key), ("@LotId", lotId), ("@DeciderId", User(decider)),
            ("@DueAt", dueAtUtc), ("@Reason", reason));

    public Task<SpResult> DisposeFailed(string key, int lotId, string inspector, int qty, string action, string? reason) =>
        CallAsync("dbo.usp_DisposeFailed",
            ("@RequestKey", key), ("@LotId", lotId), ("@InspectorId", User(inspector)), ("@Qty", qty),
            ("@Action", action), ("@Reason", reason));

    public Task<SpResult> ApproveShipment(string key, int lotId, string approver, int qty,
        string decision = "Approved", string? reason = null) =>
        CallAsync("dbo.usp_ApproveShipment",
            ("@RequestKey", key), ("@LotId", lotId), ("@ApproverId", User(approver)), ("@Qty", qty),
            ("@Decision", decision), ("@Reason", reason));

    public Task<SpResult> ShipLot(string key, int lotId, string shipper, int qty, string destination = "테스트 출하처") =>
        CallAsync("dbo.usp_ShipLot",
            ("@RequestKey", key), ("@LotId", lotId), ("@ShipperId", User(shipper)), ("@Qty", qty),
            ("@Destination", destination));

    private async Task<SpResult> CallAsync(string procedure, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(procedure, connection) { CommandType = CommandType.StoredProcedure };
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "SP 결과 행이 없습니다.");
        var json = JsonDocument.Parse(reader.GetString(0)).RootElement.Clone();
        return new SpResult(json, reader.GetBoolean(1));
    }

    public async Task<Quantities> QuantitiesAsync(int lotId)
    {
        await using var connection = await OpenAdminAsync();
        await using var command = new SqlCommand(
            "SELECT Uninspected, Passed, Failed, Held, Shipped, Reserved, Scrapped FROM LotQuantities WHERE LotId = @id", connection);
        command.Parameters.AddWithValue("@id", lotId);
        await using var r = await command.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return new Quantities(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6));
    }

    /// <summary>Ledger를 필드별로 다시 합산한 값. 앱 계산을 쓰지 않는다.</summary>
    public async Task<Quantities> LedgerSumsAsync(int lotId)
    {
        await using var connection = await OpenAdminAsync();
        await using var command = new SqlCommand("""
            SELECT
                COALESCE(SUM(CASE WHEN DeltaField = 'Uninspected' THEN Delta END), 0),
                COALESCE(SUM(CASE WHEN DeltaField = 'Passed' THEN Delta END), 0),
                COALESCE(SUM(CASE WHEN DeltaField = 'Failed' THEN Delta END), 0),
                COALESCE(SUM(CASE WHEN DeltaField = 'Held' THEN Delta END), 0),
                COALESCE(SUM(CASE WHEN DeltaField = 'Shipped' THEN Delta END), 0),
                COALESCE(SUM(CASE WHEN DeltaField = 'Reserved' THEN Delta END), 0),
                COALESCE(SUM(CASE WHEN DeltaField = 'Scrapped' THEN Delta END), 0)
            FROM Ledger WHERE LotId = @id
            """, connection);
        command.Parameters.AddWithValue("@id", lotId);
        await using var r = await command.ExecuteReaderAsync();
        await r.ReadAsync();
        return new Quantities(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6));
    }

    public async Task<string> StatusAsync(int lotId) =>
        (string)(await ScalarAsync("SELECT Status FROM Lots WHERE Id = @id", ("@id", lotId)))!;

    public async Task<int> CountAsync(string table, int lotId) =>
        (int)(await ScalarAsync($"SELECT COUNT(*) FROM {table} WHERE LotId = @id", ("@id", lotId)))!;

    public async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAdminAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync();
    }

    /// <summary>앱 계정으로 임의 SQL 실행 (권한 거부 확인용)</summary>
    public async Task ExecuteAsAppAsync(string sql)
    {
        await using var connection = new SqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>LotQuantities와 Ledger 합이 같은지 확인한다.</summary>
    public async Task AssertLedgerMatchesAsync(int lotId) =>
        Assert.Equal(await LedgerSumsAsync(lotId), await QuantitiesAsync(lotId));

    private async Task<SqlConnection> OpenAdminAsync()
    {
        var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public static string Key() => Guid.NewGuid().ToString("N");
}

public static class SqlAssert
{
    /// <summary>SP가 지정한 THROW 번호로 실패하는지 확인한다.</summary>
    public static async Task<SqlException> ThrowsAsync(int number, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<SqlException>(action);
        Assert.Equal(number, ex.Number);
        return ex;
    }
}
