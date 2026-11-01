using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using QcTrace.IntegrationTests.Infrastructure;
using QcTrace.Web.Data.Entities;

namespace QcTrace.IntegrationTests.Api;

/// <summary>비밀번호 규칙: 6자 이상, 숫자·특수문자 필요, 대·소문자는 요구하지 않음 (데모 비밀번호 12341234!)</summary>
[Collection(SqlServerCollection.Name)]
public sealed class PasswordPolicyTests(SqlServerFixture fixture) : IDisposable
{
    private readonly ApiFactory _factory = new(fixture);

    public void Dispose() => _factory.Dispose();

    [Theory(DisplayName = "비밀번호 규칙: 12341234!는 통과, 특수문자·숫자 없음과 6자 미만은 거절")]
    [InlineData("12341234!", true)]
    [InlineData("QcTrace!2026", true)]
    [InlineData("12341234", false)]
    [InlineData("abcdef!", false)]
    [InlineData("1234!", false)]
    public async Task Policy(string password, bool valid)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = "policy-check" };
        var results = await Task.WhenAll(users.PasswordValidators.Select(v => v.ValidateAsync(users, user, password)));
        Assert.Equal(valid, results.All(r => r.Succeeded));
    }
}
