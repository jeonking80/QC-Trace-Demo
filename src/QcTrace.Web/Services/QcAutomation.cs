using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;

namespace QcTrace.Web.Services;

/// <summary>
/// 운영 자동화(Hosted Service). 1분마다 깨어나서
/// 1) 검사 지연 LOT(의뢰 후 48시간 초과)을 찾아 처음 발견한 것만 DelayAlerts에 기록하고
/// 2) 한국 시간 Reports:RunAtKst(기본 08:00)가 지났는데 전날 보고서 파일이 없으면 만든다.
/// 앱이 08:00에 꺼져 있었어도 다음에 켜질 때 그날 보고서를 만든다(놓친 실행 보충). 파일이 있으면 다시 만들지 않는다.
/// 한 대의 앱 인스턴스를 전제로 한다(여러 대면 같은 파일을 동시에 만들 수 있으나 임시 파일 후 교체라 깨지지 않는다).
/// </summary>
public sealed class QcAutomation(IServiceScopeFactory scopes, ReportStorage storage, TimeProvider time,
    IConfiguration configuration, ILogger<QcAutomation> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    public TimeOnly RunAt { get; } = TimeOnly.TryParse(configuration["Reports:RunAtKst"], out var t) ? t : new TimeOnly(8, 0);
    private readonly bool _enabled = configuration.GetValue("Reports:Enabled", true);

    /// <summary>지금 있어야 할 보고서의 기준일. 08:00 이후면 어제, 이전이면 그저께.</summary>
    public static DateOnly DueBusinessDate(DateTime nowUtc, TimeOnly runAt)
    {
        var kst = Kst.FromUtc(nowUtc);
        var today = DateOnly.FromDateTime(kst);
        return TimeOnly.FromDateTime(kst) >= runAt ? today.AddDays(-1) : today.AddDays(-2);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            logger.LogInformation("Reports:Enabled=false라 자동화를 끕니다.");
            return;
        }
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // DB가 잠깐 내려가도 작업은 멈추지 않고 다음 주기에 다시 한다
                logger.LogWarning(ex, "자동화 작업 실패, 다음 주기에 다시 시도합니다.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public sealed record RunResult(int NewAlerts, string? GeneratedReport);

    public async Task<RunResult> RunOnceAsync(CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<QcTraceDbContext>();
        var now = time.GetUtcNow().UtcDateTime;

        var newAlerts = await RecordDelayAlertsAsync(db, now, ct);

        string? generated = null;
        var due = DueBusinessDate(now, RunAt);
        if (!storage.Exists(due))
        {
            var file = await scope.ServiceProvider.GetRequiredService<DailyReport>().GenerateAsync(due, ct);
            generated = file.Name;
            logger.LogInformation("일일 보고서 생성: {File} ({Size} bytes)", file.Name, file.Size);
        }
        return new RunResult(newAlerts, generated);
    }

    private async Task<int> RecordDelayAlertsAsync(QcTraceDbContext db, DateTime now, CancellationToken ct)
    {
        var delayed = await QcQueries.DelayedLotsAsync(db, now, ct);
        if (delayed.Count == 0) return 0;
        var ids = delayed.Select(d => d.LotId).ToList();
        var known = await db.DelayAlerts.AsNoTracking().Where(a => ids.Contains(a.LotId)).Select(a => a.LotId).ToListAsync(ct);
        var added = 0;
        foreach (var d in delayed.Where(d => !known.Contains(d.LotId)))
        {
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO dbo.DelayAlerts (LotId, InspectionRequestedAt, UninspectedAtDetection, DetectedAt)
                    VALUES ({d.LotId}, {d.RequestedAt}, {d.Uninspected}, {now})
                    """, ct);
                added++;
                logger.LogWarning("검사 지연 알림: {LotNo} 의뢰 후 {Hours}시간, 미검사 {Qty}개", d.LotNo, d.Hours, d.Uninspected);
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                // 다른 인스턴스가 먼저 기록함
            }
        }
        return added;
    }
}
