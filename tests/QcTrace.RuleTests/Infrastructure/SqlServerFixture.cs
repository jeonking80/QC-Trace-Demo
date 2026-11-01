using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using Testcontainers.MsSql;

namespace QcTrace.RuleTests.Infrastructure;

/// <summary>
/// 규칙 테스트 전체가 함께 쓰는 SQL Server 하나.
/// 기본은 Testcontainers로 SQL Server 2022 컨테이너를 띄운다.
/// 환경변수 QCTRACE_RULETEST_SQL(관리자 연결 문자열)이 있으면 그 서버에 새 DB를 만들어 쓴다.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string AppUser = "qctrace_ruletest_app";
    public const string AppPassword = "RuleTest!App2026";

    private MsSqlContainer? _container;

    public string AdminCs { get; private set; } = "";
    public string AppCs { get; private set; } = "";
    public TestDb Db { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("QCTRACE_RULETEST_SQL");
        string serverCs;
        if (!string.IsNullOrWhiteSpace(external))
        {
            serverCs = external;
        }
        else
        {
            _container = new MsSqlBuilder()
                .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
                .Build();
            await _container.StartAsync();
            serverCs = _container.GetConnectionString();
        }

        var dbName = "QcTraceRuleTests_" + Guid.NewGuid().ToString("N")[..8];
        AdminCs = new SqlConnectionStringBuilder(serverCs)
        {
            InitialCatalog = dbName,
            TrustServerCertificate = true,
        }.ConnectionString;
        AppCs = new SqlConnectionStringBuilder(AdminCs)
        {
            UserID = AppUser,
            Password = AppPassword,
            Pooling = true,
            MaxPoolSize = 100,
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<QcTraceDbContext>().UseSqlServer(AdminCs).Options;
        await using (var ctx = new QcTraceDbContext(options))
        {
            await ctx.Database.MigrateAsync();
        }
        await DatabaseSetup.EnsureAppLoginAsync(AdminCs, AppCs);

        Db = new TestDb(AdminCs, AppCs);
        await Db.EnsureRolesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class SqlCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sql";
}
