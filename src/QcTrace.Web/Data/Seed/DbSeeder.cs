using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data.Entities;

namespace QcTrace.Web.Data.Seed;

public static class Roles
{
    public const string Admin = "Admin";         // 관리자
    public const string Producer = "Producer";   // 생산 담당
    public const string Inspector = "Inspector"; // 검사자
    public const string Approver = "Approver";   // 승인권자
    public const string Shipper = "Shipper";     // 출하 담당

    public static readonly string[] All = [Admin, Producer, Inspector, Approver, Shipper];
}

/// <summary>
/// 첫 실행 시 기준 데이터를 적재한다.
/// 역할·사용자는 비밀번호 해시가 필요해 C#(Identity)으로, 품목·LOT·원장은 seed.sql로 넣는다.
/// 두 단계 모두 이미 데이터가 있으면 건너뛴다.
/// </summary>
public static class DbSeeder
{
    // Role이 null인 계정은 역할 없는 사용자의 403 확인용이다
    private static readonly (string UserName, string DisplayName, string? Role)[] DemoUsers =
    [
        ("admin", "관리자", Roles.Admin),
        ("producer", "생산 담당", Roles.Producer),
        ("inspector1", "검사자 1", Roles.Inspector),
        ("inspector2", "검사자 2", Roles.Inspector),
        ("approver", "승인권자", Roles.Approver),
        ("shipper", "출하 담당", Roles.Shipper),
        ("norole", "역할 없음 (권한 확인용)", null),
    ];

    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration, ILogger logger)
    {
        var db = services.GetRequiredService<QcTraceDbContext>();
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = services.GetRequiredService<UserManager<AppUser>>();
        var password = configuration["Seed:DemoPassword"] ?? "QcTrace!2026";

        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                Check(await roleManager.CreateAsync(new IdentityRole(role)), $"역할 {role}");
            }
        }

        foreach (var (userName, displayName, role) in DemoUsers)
        {
            if (await userManager.FindByNameAsync(userName) is not null) continue;

            var user = new AppUser { UserName = userName, DisplayName = displayName, EmailConfirmed = true };
            Check(await userManager.CreateAsync(user, password), $"사용자 {userName}");
            if (role is not null)
            {
                Check(await userManager.AddToRoleAsync(user, role), $"사용자 {userName} 역할");
            }
        }

        if (await db.Lots.AnyAsync())
        {
            logger.LogInformation("시드: LOT 데이터가 이미 있어 seed.sql을 건너뜁니다.");
            return;
        }

        // seed.sql은 스크립트 안에서 트랜잭션을 연다. EF의 문자열 포맷을 거치지 않도록 SqlCommand로 직접 실행한다.
        var sql = await ReadSeedScriptAsync();
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        logger.LogInformation("시드: 품목 {Items}종, LOT {Lots}건, 원장 {Ledger}행 적재",
            await db.Items.CountAsync(), await db.Lots.CountAsync(), await db.Ledger.CountAsync());
    }

    private static async Task<string> ReadSeedScriptAsync()
    {
        const string name = "QcTrace.Web.Data.Seed.seed.sql";
        await using var stream = typeof(DbSeeder).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"포함 리소스 {name}이 없습니다.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static void Check(IdentityResult result, string what)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"시드 실패 ({what}): {string.Join(", ", result.Errors.Select(e => e.Description))}");
        }
    }
}
