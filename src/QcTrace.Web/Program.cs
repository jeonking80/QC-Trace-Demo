using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.WebEncoders;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Api;
using QcTrace.Web.Auth;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;
using QcTrace.Web.Health;
using QcTrace.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages(options =>
{
    foreach (var (folder, policy) in Policies.PageFolders)
    {
        options.Conventions.AuthorizeFolder(folder, policy);
    }
    options.Conventions.AllowAnonymousToFolder("/Account");
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AllowAnonymousToPage("/Status");
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddSingleton<QcCommands>();
builder.Services.AddScoped<MasterData>();

// Razor 출력에서 한글을 &#x...; 문자 참조로 바꾸지 않는다
builder.Services.Configure<WebEncoderOptions>(options =>
    options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));

// Minimal API 응답에서 한글을 \uXXXX로 바꾸지 않는다
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All));

builder.Services.AddDbContext<QcTraceDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("QcTrace"),
        // 일시 장애만 짧게 재시도한다. 길게 재시도하면 DB가 꺼졌을 때 API 응답이 늦어진다.
        sql => sql.EnableRetryOnFailure(maxRetryCount: 2, maxRetryDelay: TimeSpan.FromSeconds(1), errorNumbersToAdd: null)));

// Identity 쿠키 로그인. 계정은 시드와 관리자 화면에서만 만든다(회원 가입 없음).
builder.Services.AddIdentity<AppUser, IdentityRole>(options =>
    {
        options.User.RequireUniqueEmail = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    })
    .AddEntityFrameworkStores<QcTraceDbContext>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "QcTrace.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.LoginPath = "/Account/Login";
    options.Events.OnRedirectToLogin = AuthResponses.OnRedirectToLogin;
    options.Events.OnRedirectToAccessDenied = AuthResponses.OnRedirectToAccessDenied;
});

// 쿠키의 SecurityStamp를 1분마다 DB와 대조한다. 역할 변경·잠금·로그아웃이 기존 쿠키에도 1분 안에 반영된다.
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
    options.ValidationInterval = TimeSpan.FromMinutes(1));

builder.Services.AddAuthorization(Policies.Configure);

// 쿠키 암호화 키를 볼륨에 보관해 컨테이너를 다시 만들어도 로그인이 유지되게 한다
var keysPath = builder.Configuration["DataProtection:KeysPath"];
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("QcTrace");
if (!string.IsNullOrWhiteSpace(keysPath))
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

// /health: 앱이 떠 있고 SQL Server에 연결되면 200, DB 연결 실패 시 503
builder.Services.AddHealthChecks()
    .AddCheck<SqlServerHealthCheck>("sqlserver");

var healthJsonOptions = new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
};

var app = builder.Build();

// 첫 실행 시 마이그레이션·앱 DB 계정·시드 (Database:MigrateOnStartup=false로 끌 수 있다)
if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    await DatabaseSetup.InitializeAsync(scope.ServiceProvider, app.Configuration, app.Logger);
}

// /api 예외는 ApiExceptionHandler가 ProblemDetails로, 화면 예외는 /Error 페이지로 보낸다
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}
// 403·404 등 본문 없는 오류 응답은 상태 코드를 유지한 채 안내 페이지를 보여준다
app.UseStatusCodePagesWithReExecute("/Status", "?code={0}");
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapLotEndpoints();
app.MapCommandEndpoints();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                durationMs = (int)e.Value.Duration.TotalMilliseconds
            })
        };
        await context.Response.WriteAsync(JsonSerializer.Serialize(body, healthJsonOptions));
    }
}).AllowAnonymous();

await app.RunAsync();

// 통합 테스트(WebApplicationFactory)가 진입점을 찾을 수 있게 공개한다
public partial class Program;
