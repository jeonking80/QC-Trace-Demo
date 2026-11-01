using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace QcTrace.IntegrationTests.Infrastructure;

/// <summary>
/// 실제 앱(Program)을 테스트 서버로 띄운다. DB는 SqlServerFixture 컨테이너, 앱 연결은 최소 권한 계정.
/// 로그인 쿠키 대신 X-Test-User 헤더로 사용자를 정한다. 역할 정책·SP 규칙·오류 매핑은 실제 코드 그대로다.
/// </summary>
public sealed class ApiFactory(SqlServerFixture fixture) : WebApplicationFactory<Program>
{
    public const string UserHeader = "X-Test-User";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:QcTrace", fixture.AppConnectionString);
        builder.UseSetting("ConnectionStrings:QcTraceAdmin", fixture.AdminConnectionString);
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(fixture);
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthenticationOptions>(o =>
            {
                o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                o.DefaultForbidScheme = TestAuthHandler.SchemeName;
            });
        });
    }

    public HttpClient ClientFor(string userName)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(UserHeader, userName);
        return client;
    }
}

public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, SqlServerFixture fixture)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userName = Request.Headers[ApiFactory.UserHeader].ToString();
        if (string.IsNullOrEmpty(userName) || !fixture.UserIds.TryGetValue(userName, out var id))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id), new(ClaimTypes.Name, userName) };
        claims.AddRange(fixture.RolesOf(userName).Select(r => new Claim(ClaimTypes.Role, r)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
