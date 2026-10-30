using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Api;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;
using QcTrace.Web.Data.Seed;
using QcTrace.Web.Health;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();

// Minimal API 응답에서 한글을 \uXXXX로 바꾸지 않는다
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All));

builder.Services.AddDbContext<QcTraceDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("QcTrace"),
        sql => sql.EnableRetryOnFailure()));

// 로그인(쿠키)은 작업 단위 3에서 붙인다. 지금은 사용자·역할 저장소만 등록한다.
builder.Services.AddIdentityCore<AppUser>()
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<QcTraceDbContext>();

// /health: 앱이 떠 있고 SQL Server에 연결되면 200, DB 연결 실패 시 503
builder.Services.AddHealthChecks()
    .AddCheck<SqlServerHealthCheck>("sqlserver");

var healthJsonOptions = new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
};

var app = builder.Build();

// 첫 실행 시 마이그레이션 적용과 시드 적재 (Database:MigrateOnStartup=false로 끌 수 있다)
if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<QcTraceDbContext>();
    await db.Database.MigrateAsync();
    await DbSeeder.SeedAsync(scope.ServiceProvider, app.Configuration, app.Logger);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();
app.MapLotEndpoints();

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
});

await app.RunAsync();
