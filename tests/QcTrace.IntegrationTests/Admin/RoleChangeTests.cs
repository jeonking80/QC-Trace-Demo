using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using QcTrace.IntegrationTests.Infrastructure;

namespace QcTrace.IntegrationTests.Admin;

/// <summary>역할 변경은 usp_SetUserRoles로만, 바뀐 역할마다 RoleChanges에 추가 전용 기록(2026-11-01 사용자 결정)</summary>
[Collection(SqlServerCollection.Name)]
public class RoleChangeTests(SqlServerFixture fixture)
{
    private readonly Db _db = new(fixture);

    private async Task<JsonElement> SetRoles(string actor, string userId, string roles, string? reason)
    {
        await using var connection = new SqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("dbo.usp_SetUserRoles", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@ActorId", fixture.UserIds[actor]);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@Roles", roles);
        command.Parameters.AddWithValue("@Reason", (object?)reason ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return JsonDocument.Parse(reader.GetString(0)).RootElement.Clone();
    }

    private async Task<string> NewUserAsync()
    {
        var id = "user-t-" + Guid.NewGuid().ToString("N")[..12];
        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            INSERT INTO Users (Id, UserName, NormalizedUserName, EmailConfirmed, SecurityStamp, ConcurrencyStamp,
                               PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount, DisplayName)
            VALUES (@id, @id, UPPER(@id), 1, N'stamp-0', NEWID(), 0, 0, 1, 0, @id)
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<string> RolesOfAsync(string userId) =>
        await _db.ScalarAsync("""
            SELECT STRING_AGG(r.Name, ',') WITHIN GROUP (ORDER BY r.Name) FROM UserRoles ur JOIN Roles r ON r.Id = ur.RoleId WHERE ur.UserId = @u
            """, ("@u", userId)) as string ?? "";

    [Fact(DisplayName = "역할 추가·제거가 반영되고 바뀐 역할마다 작업자·사유가 담긴 변경 행이 남는다")]
    public async Task SetRoles_RecordsEachChange()
    {
        var user = await NewUserAsync();
        var first = await SetRoles("admin", user, "Inspector,Approver", "검사·승인 겸직 배치");
        var second = await SetRoles("admin", user, "Approver,Shipper", "검사 업무 종료");

        Assert.Equal("Approver,Shipper", await RolesOfAsync(user));
        Assert.Equal(["Inspector"], second.GetProperty("removed").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(2, first.GetProperty("added").GetArrayLength());
        Assert.Equal(4, (int)(await _db.ScalarAsync("SELECT COUNT(*) FROM RoleChanges WHERE UserId = @u", ("@u", user)))!);
        Assert.Equal(1, (int)(await _db.ScalarAsync("""
            SELECT COUNT(*) FROM RoleChanges WHERE UserId = @u AND RoleName = N'Inspector' AND Change = N'Remove'
              AND ActorId = @a AND Reason = N'검사 업무 종료'
            """, ("@u", user), ("@a", fixture.UserIds["admin"])))!);
        Assert.NotEqual("stamp-0", (string)(await _db.ScalarAsync("SELECT SecurityStamp FROM Users WHERE Id = @u", ("@u", user)))!);
    }

    [Fact(DisplayName = "같은 역할 목록을 다시 보내면 아무것도 바뀌지 않고 기록도 남지 않는다")]
    public async Task SameRoles_NoChange()
    {
        var user = await NewUserAsync();
        await SetRoles("admin", user, "Producer", "배치");
        var again = await SetRoles("admin", user, "Producer", "재전송");

        Assert.Equal(0, again.GetProperty("added").GetArrayLength() + again.GetProperty("removed").GetArrayLength());
        Assert.Equal(1, (int)(await _db.ScalarAsync("SELECT COUNT(*) FROM RoleChanges WHERE UserId = @u", ("@u", user)))!);
    }

    [Fact(DisplayName = "역할 변경 거절: 사유 없음 52004, 관리자 아님 50403, 없는 역할 50400, 없는 사용자 50404")]
    public async Task SetRoles_Rejections()
    {
        var user = await NewUserAsync();
        await SqlAssert.ThrowsAsync(52004, () => SetRoles("admin", user, "Producer", "  "));
        await SqlAssert.ThrowsAsync(50403, () => SetRoles("approver", user, "Producer", "권한 없음"));
        await SqlAssert.ThrowsAsync(50400, () => SetRoles("admin", user, "Producer,Boss", "없는 역할"));
        await SqlAssert.ThrowsAsync(50404, () => SetRoles("admin", "no-such-user", "Producer", "없는 사용자"));
        Assert.Equal("", await RolesOfAsync(user));
    }

    [Fact(DisplayName = "자기 관리자 역할은 뺄 수 없다(50400), 다른 관리자는 뺄 수 있다")]
    public async Task CannotRemoveOwnAdmin()
    {
        var admin3 = await NewUserAsync();
        await SetRoles("admin", admin3, "Admin", "관리자 추가");

        await SqlAssert.ThrowsAsync(50400, () => SetRoles("admin", fixture.UserIds["admin"], "Producer", "실수"));
        await SetRoles("admin", admin3, "", "관리자 해제");
        Assert.Equal("", await RolesOfAsync(admin3));
    }

    [Theory(DisplayName = "앱 계정은 UserRoles·RoleChanges에 직접 쓸 수 없다(229)")]
    [InlineData("INSERT INTO UserRoles (UserId, RoleId) VALUES (N'user-norole', N'role-Admin')")]
    [InlineData("DELETE FROM UserRoles WHERE UserId = N'user-producer'")]
    [InlineData("INSERT INTO RoleChanges (UserId, RoleName, Change, ActorId, Reason) VALUES (N'user-norole', N'Admin', N'Add', N'user-admin', N'x')")]
    public async Task AppCannotWriteRolesDirectly(string sql)
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => _db.ExecuteAsAppAsync(sql));
        Assert.Equal(229, ex.Number);
    }

    [Fact(DisplayName = "역할 변경 이력은 관리자 계정이어도 UPDATE·DELETE가 트리거로 거부된다(51000)")]
    public async Task RoleChanges_AppendOnly()
    {
        var user = await NewUserAsync();
        await SetRoles("admin", user, "Shipper", "배치");
        foreach (var sql in new[] { "UPDATE RoleChanges SET Reason = N'변조' WHERE UserId = @u", "DELETE FROM RoleChanges WHERE UserId = @u" })
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => _db.ScalarAsync(sql, ("@u", user)));
            Assert.Equal(51000, ex.Number);
        }
    }
}
