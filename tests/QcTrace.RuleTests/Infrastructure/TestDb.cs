using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace QcTrace.RuleTests.Infrastructure;

public sealed record SpResult(JsonElement Json, bool Replayed, string RawJson);

public sealed record Quantities(int Uninspected, int Passed, int Failed, int Held, int Shipped, int Reserved)
{
    public int Available => Passed - Shipped - Reserved;
}

public sealed record LedgerRow(long Id, string Kind, string DeltaField, int Delta, string ActorId, string Reason,
    string? SourceTable, long? SourceId, DateTime CreatedAt);

/// <summary>
/// 테스트용 DB 도구. 준비 데이터는 관리자 연결로 넣고, 저장 프로시저는 앱 계정으로 부른다.
/// 수량 계산은 여기서 하지 않는다. 테이블 값을 그대로 읽어 테스트에서 비교한다.
/// </summary>
public sealed class TestDb(string adminCs, string appCs)
{
    public static readonly string[] AllRoles = ["Admin", "Producer", "Inspector", "Approver", "Shipper"];

    public string AdminCs { get; } = adminCs;
    public string AppCs { get; } = appCs;

    // ---------- 준비 데이터 (관리자 연결) ----------

    public async Task EnsureRolesAsync()
    {
        foreach (var role in AllRoles)
        {
            await ExecAdminAsync("""
                IF NOT EXISTS (SELECT 1 FROM Roles WHERE Name = @n)
                    INSERT INTO Roles (Id, Name, NormalizedName, ConcurrencyStamp)
                    VALUES (CONVERT(nvarchar(450), NEWID()), @n, UPPER(@n), CONVERT(nvarchar(450), NEWID()));
                """, ("@n", role));
        }
    }

    /// <summary>사용자를 만들고 주어진 역할을 붙인다. 반환값은 Users.Id</summary>
    public async Task<string> CreateUserAsync(params string[] roles)
    {
        var id = Guid.NewGuid().ToString();
        var userName = "rt_" + id[..8];
        await ExecAdminAsync("""
            INSERT INTO Users (Id, UserName, NormalizedUserName, EmailConfirmed, SecurityStamp, ConcurrencyStamp,
                               PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount, DisplayName)
            VALUES (@id, @u, UPPER(@u), 1, CONVERT(nvarchar(450), NEWID()), CONVERT(nvarchar(450), NEWID()),
                    0, 0, 0, 0, @u);
            """, ("@id", id), ("@u", userName));
        foreach (var role in roles)
        {
            await ExecAdminAsync("""
                INSERT INTO UserRoles (UserId, RoleId) SELECT @id, Id FROM Roles WHERE Name = @r;
                """, ("@id", id), ("@r", role));
        }
        return id;
    }

    private int _itemId;

    private async Task<int> GetItemIdAsync()
    {
        if (_itemId != 0) return _itemId;
        var code = "RT-" + Guid.NewGuid().ToString("N")[..8];
        _itemId = await ScalarAdminAsync<int>("""
            INSERT INTO Items (ItemCode, Name, IsActive) VALUES (@c, N'규칙 테스트 품목', 1);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """, ("@c", code));
        return _itemId;
    }

    /// <summary>검사 대기(Waiting) LOT 1건을 계약서 방식대로 직접 넣는다. 반환값은 Lots.Id</summary>
    public async Task<int> CreateWaitingLotAsync(int producedQty, string? producerId = null)
    {
        producerId ??= await CreateUserAsync("Producer");
        var itemId = await GetItemIdAsync();
        var lotNo = "RT" + Guid.NewGuid().ToString("N")[..16];
        return await ScalarAdminAsync<int>("""
            SET XACT_ABORT ON;
            BEGIN TRAN;
            INSERT INTO Lots (LotNo, ItemId, ProducedQty, ProducedOn, Shift, Status, CreatedById, InspectionRequestedAt)
            VALUES (@no, @item, @qty, CAST(SYSUTCDATETIME() AS date), N'Day', N'Waiting', @by, SYSUTCDATETIME());
            DECLARE @lot int = CAST(SCOPE_IDENTITY() AS int);
            INSERT INTO LotQuantities (LotId, Uninspected, Passed, Failed, Held, Shipped, Reserved)
            VALUES (@lot, @qty, 0, 0, 0, 0, 0);
            INSERT INTO Ledger (LotId, Kind, DeltaField, Delta, ActorId, Reason, SourceTable, SourceId)
            VALUES (@lot, N'Produce', N'Uninspected', @qty, @by, N'생산 등록', N'Lots', @lot);
            COMMIT;
            SELECT @lot;
            """, ("@no", lotNo), ("@item", itemId), ("@qty", producedQty), ("@by", producerId));
    }

    // ---------- 저장 프로시저 (앱 계정) ----------

    public static string NewKey() => "rt-" + Guid.NewGuid().ToString("N");

    public Task<SpResult> ApplyInspectionAsync(string key, int lotId, string inspectorId, int qty, string verdict,
        string? reason = null, long? correctsInspectionId = null, string? measuredJson = null)
        => CallSpAsync("dbo.usp_ApplyInspection",
            ("@RequestKey", key), ("@LotId", lotId), ("@InspectorId", inspectorId), ("@Qty", qty),
            ("@Verdict", verdict), ("@MeasuredJson", measuredJson), ("@Reason", reason),
            ("@CorrectsInspectionId", correctsInspectionId));

    public Task<SpResult> ApproveShipmentAsync(string key, int lotId, string approverId, int qty,
        string decision = "Approved", string? reason = null)
        => CallSpAsync("dbo.usp_ApproveShipment",
            ("@RequestKey", key), ("@LotId", lotId), ("@ApproverId", approverId), ("@Qty", qty),
            ("@Decision", decision), ("@Reason", reason));

    public Task<SpResult> ShipLotAsync(string key, int lotId, string shipperId, int qty, string destination = "고객사A")
        => CallSpAsync("dbo.usp_ShipLot",
            ("@RequestKey", key), ("@LotId", lotId), ("@ShipperId", shipperId), ("@Qty", qty),
            ("@Destination", destination));

    private async Task<SpResult> CallSpAsync(string sp, params (string Name, object? Value)[] args)
    {
        await using var conn = new SqlConnection(AppCs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sp, conn) { CommandType = CommandType.StoredProcedure, CommandTimeout = 60 };
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException($"{sp}가 결과 행을 돌려주지 않았습니다.");
        var raw = reader.GetString(reader.GetOrdinal("ResponseJson"));
        var replayed = reader.GetBoolean(reader.GetOrdinal("Replayed"));
        using var doc = JsonDocument.Parse(raw);
        return new SpResult(doc.RootElement.Clone(), replayed, raw);
    }

    // ---------- 조회 (관리자 연결) ----------

    public async Task<Quantities> GetQuantitiesAsync(int lotId)
    {
        await using var conn = new SqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT Uninspected, Passed, Failed, Held, Shipped, Reserved FROM LotQuantities WHERE LotId = @id", conn);
        cmd.Parameters.AddWithValue("@id", lotId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) throw new InvalidOperationException($"LotQuantities에 LOT {lotId} 없음");
        return new Quantities(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5));
    }

    /// <summary>Ledger의 DeltaField별 Delta 합 (앱 계산 코드를 쓰지 않고 SQL로 직접 집계)</summary>
    public async Task<Quantities> GetLedgerSumsAsync(int lotId)
    {
        await using var conn = new SqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("""
            SELECT
              ISNULL(SUM(CASE WHEN DeltaField = 'Uninspected' THEN Delta END), 0),
              ISNULL(SUM(CASE WHEN DeltaField = 'Passed' THEN Delta END), 0),
              ISNULL(SUM(CASE WHEN DeltaField = 'Failed' THEN Delta END), 0),
              ISNULL(SUM(CASE WHEN DeltaField = 'Held' THEN Delta END), 0),
              ISNULL(SUM(CASE WHEN DeltaField = 'Shipped' THEN Delta END), 0),
              ISNULL(SUM(CASE WHEN DeltaField = 'Reserved' THEN Delta END), 0)
            FROM Ledger WHERE LotId = @id
            """, conn);
        cmd.Parameters.AddWithValue("@id", lotId);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return new Quantities(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5));
    }

    public async Task<List<LedgerRow>> GetLedgerAsync(int lotId)
    {
        var rows = new List<LedgerRow>();
        await using var conn = new SqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("""
            SELECT Id, Kind, DeltaField, Delta, ActorId, Reason, SourceTable, SourceId, CreatedAt
            FROM Ledger WHERE LotId = @id ORDER BY Id
            """, conn);
        cmd.Parameters.AddWithValue("@id", lotId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            rows.Add(new LedgerRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3),
                r.IsDBNull(4) ? "" : r.GetString(4), r.IsDBNull(5) ? "" : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetInt64(7), r.GetDateTime(8)));
        }
        return rows;
    }

    public Task<string> GetLotStatusAsync(int lotId)
        => ScalarAdminAsync<string>("SELECT Status FROM Lots WHERE Id = @id", ("@id", lotId));

    public Task<int> CountAsync(string table, int lotId)
        => ScalarAdminAsync<int>($"SELECT COUNT(*) FROM {table} WHERE LotId = @id", ("@id", lotId));

    public Task<int> CountRequestsAsync(string key)
        => ScalarAdminAsync<int>("SELECT COUNT(*) FROM QcRequests WHERE RequestKey = @k", ("@k", key));

    // ---------- 저수준 ----------

    public async Task ExecAdminAsync(string sql, params (string Name, object? Value)[] args)
    {
        await using var conn = new SqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ExecAppAsync(string sql, params (string Name, object? Value)[] args)
    {
        await using var conn = new SqlConnection(AppCs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAdminAsync<T>(string sql, params (string Name, object? Value)[] args)
    {
        await using var conn = new SqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var result = await cmd.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T));
    }
}
