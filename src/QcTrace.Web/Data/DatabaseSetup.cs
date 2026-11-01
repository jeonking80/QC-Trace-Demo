using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data.Seed;

namespace QcTrace.Web.Data;

/// <summary>
/// 앱 시작 시 DB 준비. 관리자 연결(ConnectionStrings:QcTraceAdmin)로 마이그레이션·앱 계정 생성·시드를 하고,
/// 앱은 실행 중에 최소 권한 계정(ConnectionStrings:QcTrace)만 쓴다.
/// </summary>
public static class DatabaseSetup
{
    public const string AppRole = "qctrace_app_role";

    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration, ILogger logger)
    {
        var adminConnectionString = configuration.GetConnectionString("QcTraceAdmin");
        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            logger.LogWarning("ConnectionStrings:QcTraceAdmin이 없어 마이그레이션과 시드를 건너뜁니다.");
            return;
        }

        var options = new DbContextOptionsBuilder<QcTraceDbContext>()
            .UseSqlServer(adminConnectionString, sql => sql.EnableRetryOnFailure())
            .Options;
        await using var adminDb = new QcTraceDbContext(options);

        await adminDb.Database.MigrateAsync();
        await EnsureAppLoginAsync(adminConnectionString, configuration.GetConnectionString("QcTrace")!, logger);
        await DbSeeder.SeedAsync(services, adminDb, configuration, logger);
    }

    /// <summary>
    /// 앱 연결 문자열의 로그인을 만들고(있으면 비밀번호만 맞춘다) qctrace_app_role 역할에 넣는다.
    /// 앱 연결이 관리자와 같은 로그인이면 아무것도 하지 않는다.
    /// </summary>
    public static async Task EnsureAppLoginAsync(string adminConnectionString, string appConnectionString, ILogger? logger = null)
    {
        var admin = new SqlConnectionStringBuilder(adminConnectionString);
        var app = new SqlConnectionStringBuilder(appConnectionString);
        if (string.IsNullOrEmpty(app.UserID) || string.Equals(app.UserID, admin.UserID, StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogWarning("앱 연결이 관리자 계정({User})을 그대로 씁니다. 최소 권한 계정을 쓰려면 ConnectionStrings:QcTrace의 User Id를 바꾸세요.", app.UserID);
            return;
        }

        const string sql = """
            DECLARE @q nvarchar(300) = QUOTENAME(@login), @p nvarchar(300) = QUOTENAME(@password, '''');
            DECLARE @stmt nvarchar(max);
            IF SUSER_ID(@login) IS NULL
                SET @stmt = N'CREATE LOGIN ' + @q + N' WITH PASSWORD = ' + @p + N', CHECK_POLICY = ON, DEFAULT_DATABASE = ' + QUOTENAME(DB_NAME());
            ELSE
                SET @stmt = N'ALTER LOGIN ' + @q + N' WITH PASSWORD = ' + @p;
            EXEC (@stmt);
            IF DATABASE_PRINCIPAL_ID(@login) IS NULL
            BEGIN
                SET @stmt = N'CREATE USER ' + @q + N' FOR LOGIN ' + @q;
                EXEC (@stmt);
            END;
            IF IS_ROLEMEMBER(@role, @login) = 0 OR IS_ROLEMEMBER(@role, @login) IS NULL
            BEGIN
                SET @stmt = N'ALTER ROLE ' + QUOTENAME(@role) + N' ADD MEMBER ' + @q;
                EXEC (@stmt);
            END;
            """;

        await using var connection = new SqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@login", app.UserID);
        command.Parameters.AddWithValue("@password", app.Password);
        command.Parameters.AddWithValue("@role", AppRole);
        await command.ExecuteNonQueryAsync();
        logger?.LogInformation("앱 DB 계정 {User} 준비 완료 (역할 {Role})", app.UserID, AppRole);
    }
}
