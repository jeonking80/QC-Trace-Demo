using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using Testcontainers.MsSql;

namespace QcTrace.IntegrationTests.Infrastructure;

/// <summary>
/// 실제 SQL Server 2022 컨테이너를 띄우고 앱과 같은 마이그레이션(테이블·트리거·SP·앱 역할)을 적용한다.
/// SP 호출은 앱과 같은 최소 권한 계정(qctrace_app)으로 한다.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string AppPassword = "QcTrace!App2026";

    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string AdminConnectionString { get; private set; } = "";
    public string AppConnectionString { get; private set; } = "";

    /// <summary>UserName → Users.Id</summary>
    public Dictionary<string, string> UserIds { get; } = new();

    private int _itemId;
    public int ItemId => _itemId;

    public IEnumerable<string> RolesOf(string userName) => TestUsers.Single(u => u.UserName == userName).Roles;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        AdminConnectionString = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "QcTrace",
            TrustServerCertificate = true
        }.ConnectionString;
        AppConnectionString = new SqlConnectionStringBuilder(AdminConnectionString)
        {
            UserID = "qctrace_app",
            Password = AppPassword
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<QcTraceDbContext>().UseSqlServer(AdminConnectionString).Options;
        await using (var db = new QcTraceDbContext(options))
        {
            await db.Database.MigrateAsync();
        }
        await DatabaseSetup.EnsureAppLoginAsync(AdminConnectionString, AppConnectionString);
        await SeedUsersAndItemAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    // inspector_approver는 검사자·승인권자 역할을 둘 다 가진 사용자다(R5 확인용)
    private static readonly (string UserName, string[] Roles)[] TestUsers =
    [
        ("producer", ["Producer"]),
        ("inspector1", ["Inspector"]),
        ("inspector2", ["Inspector"]),
        ("approver", ["Approver"]),
        ("inspector_approver", ["Inspector", "Approver"]),
        ("shipper", ["Shipper"]),
        ("norole", []),
    ];

    private async Task SeedUsersAndItemAsync()
    {
        await using var connection = new SqlConnection(AdminConnectionString);
        await connection.OpenAsync();

        foreach (var role in new[] { "Admin", "Producer", "Inspector", "Approver", "Shipper" })
        {
            await Exec(connection, "INSERT INTO Roles (Id, Name, NormalizedName, ConcurrencyStamp) VALUES (@id, @n, UPPER(@n), NEWID())",
                ("@id", "role-" + role), ("@n", role));
        }

        foreach (var (userName, roles) in TestUsers)
        {
            var id = "user-" + userName;
            UserIds[userName] = id;
            await Exec(connection, """
                INSERT INTO Users (Id, UserName, NormalizedUserName, EmailConfirmed, SecurityStamp, ConcurrencyStamp,
                                   PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount, DisplayName)
                VALUES (@id, @u, UPPER(@u), 1, NEWID(), NEWID(), 0, 0, 1, 0, @u)
                """, ("@id", id), ("@u", userName));
            foreach (var role in roles)
            {
                await Exec(connection, "INSERT INTO UserRoles (UserId, RoleId) VALUES (@u, @r)", ("@u", id), ("@r", "role-" + role));
            }
        }

        await using var item = new SqlCommand(
            "INSERT INTO Items (ItemCode, Name, IsActive) OUTPUT INSERTED.Id VALUES (N'TEST-01', N'테스트 품목', 1)", connection);
        _itemId = (int)(await item.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// 검사 대기 LOT 하나를 만든다. LOT 등록 SP는 단위 6 범위라 여기서는 관리자 연결로 직접 넣는다(Ledger Produce 포함).
    /// </summary>
    public async Task<int> CreateLotAsync(int producedQty, string status = "Waiting")
    {
        await using var connection = new SqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SET XACT_ABORT ON;
            BEGIN TRAN;
            DECLARE @id int;
            INSERT INTO Lots (LotNo, ItemId, ProducedQty, ProducedOn, Shift, Status, CreatedById, InspectionRequestedAt)
            VALUES (@lotNo, @itemId, @qty, CAST(SYSUTCDATETIME() AS date), N'A조', @status, @producer, IIF(@status = N'Registered', NULL, SYSUTCDATETIME()));
            SET @id = SCOPE_IDENTITY();
            INSERT INTO LotQuantities (LotId, Uninspected, Passed, Failed, Held, Shipped, Reserved) VALUES (@id, @qty, 0, 0, 0, 0, 0);
            INSERT INTO Ledger (LotId, Kind, DeltaField, Delta, ActorId, Reason, SourceTable, SourceId)
            VALUES (@id, N'Produce', N'Uninspected', @qty, @producer, N'테스트 LOT 등록', N'Lots', @id);
            COMMIT;
            SELECT @id;
            """, connection);
        command.Parameters.AddWithValue("@lotNo", "T-" + Guid.NewGuid().ToString("N")[..20]);
        command.Parameters.AddWithValue("@itemId", _itemId);
        command.Parameters.AddWithValue("@qty", producedQty);
        command.Parameters.AddWithValue("@producer", UserIds["producer"]);
        command.Parameters.AddWithValue("@status", status);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task Exec(SqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}
