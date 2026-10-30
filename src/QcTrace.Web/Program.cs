using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using QcTrace.Web.Health;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();

// /health: 앱이 떠 있고 SQL Server에 연결되면 200, DB 연결 실패 시 503
builder.Services.AddHealthChecks()
    .AddCheck<SqlServerHealthCheck>("sqlserver");

var healthJsonOptions = new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
};

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();

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

app.Run();
