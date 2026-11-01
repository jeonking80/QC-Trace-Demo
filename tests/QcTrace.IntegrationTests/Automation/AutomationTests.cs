using System.Net;
using ClosedXML.Excel;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using QcTrace.IntegrationTests.Infrastructure;
using QcTrace.Web.Services;

namespace QcTrace.IntegrationTests.Automation;

/// <summary>단위 9 자동화: 일일 엑셀 보고서(매일 08:00 한국 시간)와 검사 지연 알림(48시간 초과)</summary>
[Collection(SqlServerCollection.Name)]
public sealed class AutomationTests(SqlServerFixture fixture) : IDisposable
{
    private readonly ApiFactory _factory = new(fixture);
    private readonly Db _db = new(fixture);

    public void Dispose() => _factory.Dispose();

    /// <summary>앱의 시계를 가짜 시계로 바꾼 서버. 자동화 한 번 실행을 직접 부른다.</summary>
    private (QcAutomation Automation, FakeTimeProvider Clock, IServiceProvider Services) WithClock(DateTimeOffset utcNow)
    {
        var clock = new FakeTimeProvider(utcNow);
        var app = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock)));
        return (app.Services.GetRequiredService<QcAutomation>(), clock, app.Services);
    }

    private static DateTimeOffset KstToUtc(DateTime kst) => new DateTimeOffset(kst, TimeSpan.FromHours(9)).ToUniversalTime();

    [Theory(DisplayName = "보고 기준일: 한국 시간 08:00이 지나면 어제, 그 전이면 그저께 보고서가 있어야 한다")]
    [InlineData("2026-11-02 07:59:59", "2026-10-31")]
    [InlineData("2026-11-02 08:00:00", "2026-11-01")]
    [InlineData("2026-11-02 23:59:59", "2026-11-01")]
    [InlineData("2026-11-03 00:00:00", "2026-11-01")]
    public void DueBusinessDate(string kst, string expected)
    {
        var utc = KstToUtc(DateTime.Parse(kst)).UtcDateTime;
        Assert.Equal(DateOnly.Parse(expected), QcAutomation.DueBusinessDate(utc, new TimeOnly(8, 0)));
    }

    [Fact(DisplayName = "08:00이 지나 자동화가 돌면 전날 보고서 파일이 생기고, 다시 돌아도 새로 만들지 않는다")]
    public async Task Scheduler_GeneratesOnce()
    {
        var (automation, clock, _) = WithClock(KstToUtc(new DateTime(2026, 10, 21, 7, 59, 0)));

        var before = await automation.RunOnceAsync();
        Assert.Equal("qc-daily-20261019.xlsx", before.GeneratedReport);   // 08:00 전: 그저께 보고서(놓친 실행 보충)

        clock.Advance(TimeSpan.FromMinutes(1));                             // 08:00
        var at8 = await automation.RunOnceAsync();
        Assert.Equal("qc-daily-20261020.xlsx", at8.GeneratedReport);

        clock.Advance(TimeSpan.FromMinutes(1));
        var again = await automation.RunOnceAsync();
        Assert.Null(again.GeneratedReport);

        Assert.True(File.Exists(Path.Combine(_factory.ReportsPath, "qc-daily-20261020.xlsx")));
        Assert.Empty(Directory.GetFiles(_factory.ReportsPath, "*.tmp"));
    }

    [Fact(DisplayName = "보고서 엑셀: 시트 5개, 요약 수치는 vw_DailyQcSummary와 같고, 그날 검사·출하·지연 LOT이 들어간다")]
    public async Task Report_Contents()
    {
        var lot = await fixture.CreateLotAsync(10);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 6, "Pass");
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 1, "Fail", reason: "흠집");
        await _db.ApproveShipment(Db.Key(), lot, "approver", 4);
        await _db.ShipLot(Db.Key(), lot, "shipper", 3, "보고서 출하처");
        var delayed = await fixture.CreateLotAsync(5);
        await _db.ScalarAsync("UPDATE Lots SET InspectionRequestedAt = DATEADD(HOUR, -50, SYSUTCDATETIME()) WHERE Id = @id", ("@id", delayed));
        var lotNo = (string)(await _db.ScalarAsync("SELECT LotNo FROM Lots WHERE Id = @id", ("@id", lot)))!;
        var delayedNo = (string)(await _db.ScalarAsync("SELECT LotNo FROM Lots WHERE Id = @id", ("@id", delayed)))!;

        var today = Kst.Today;
        using var scope = _factory.Services.CreateScope();
        var file = await scope.ServiceProvider.GetRequiredService<DailyReport>().GenerateAsync(today);
        Assert.Equal($"qc-daily-{today:yyyyMMdd}.xlsx", file.Name);

        using var wb = new XLWorkbook(Path.Combine(_factory.ReportsPath, file.Name));
        Assert.Equal(["요약", "검사 실적", "승인·출하", "검사 지연", "재고 대사"], wb.Worksheets.Select(w => w.Name).ToArray());

        int Summary(string label) => (int)wb.Worksheet("요약").Column(1).CellsUsed().Single(c => c.GetString() == label).CellRight().GetDouble();
        async Task<int> View(string column) => (int)(await _db.ScalarAsync(
            $"SELECT {column} FROM vw_DailyQcSummary WHERE SummaryDate = @d", ("@d", today.ToDateTime(TimeOnly.MinValue))))!;

        Assert.Equal(await View("InspectedQty"), Summary("검사 수량"));
        Assert.Equal(await View("PassedQty"), Summary("합격"));
        Assert.Equal(await View("FailedQty"), Summary("불합격"));
        Assert.Equal(await View("ApprovedQty"), Summary("출하 승인 수량"));
        Assert.Equal(await View("ShippedQty"), Summary("출하 수량"));
        Assert.True(Summary("검사 지연 LOT(48시간 초과)") >= 1);

        var inspections = wb.Worksheet("검사 실적").RowsUsed().Where(r => r.Cell(2).GetString() == lotNo).ToList();
        Assert.Equal(["합격", "불합격"], inspections.Select(r => r.Cell(6).GetString()).ToArray());
        var moves = wb.Worksheet("승인·출하").RowsUsed().Where(r => r.Cell(3).GetString() == lotNo).ToList();
        Assert.Equal(["출하 승인", "출하"], moves.Select(r => r.Cell(2).GetString()).ToArray());
        Assert.Equal("보고서 출하처", moves[1].Cell(6).GetString());
        Assert.Contains(wb.Worksheet("검사 지연").RowsUsed(), r => r.Cell(1).GetString() == delayedNo);
    }

    [Fact(DisplayName = "지난 날짜 보고서에는 그날 이후 실적이 들어가지 않는다")]
    public async Task Report_PastDate_IsEmptyForToday()
    {
        var lot = await fixture.CreateLotAsync(3);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 3, "Pass");
        var lotNo = (string)(await _db.ScalarAsync("SELECT LotNo FROM Lots WHERE Id = @id", ("@id", lot)))!;

        using var scope = _factory.Services.CreateScope();
        var file = await scope.ServiceProvider.GetRequiredService<DailyReport>().GenerateAsync(Kst.Today.AddDays(-3));
        using var wb = new XLWorkbook(Path.Combine(_factory.ReportsPath, file.Name));
        Assert.DoesNotContain(wb.Worksheet("검사 실적").RowsUsed(), r => r.Cell(2).GetString() == lotNo);
    }

    [Fact(DisplayName = "48시간 넘은 LOT은 처음 한 번만 지연 알림으로 기록되고, 48시간 전 LOT은 기록되지 않는다")]
    public async Task DelayAlert_RecordedOnce()
    {
        var late = await fixture.CreateLotAsync(10);
        var fresh = await fixture.CreateLotAsync(10);
        await _db.ScalarAsync("UPDATE Lots SET InspectionRequestedAt = DATEADD(HOUR, -49, SYSUTCDATETIME()) WHERE Id = @id", ("@id", late));
        await _db.ScalarAsync("UPDATE Lots SET InspectionRequestedAt = DATEADD(HOUR, -47, SYSUTCDATETIME()) WHERE Id = @id", ("@id", fresh));
        var (automation, _, _) = WithClock(DateTimeOffset.UtcNow);

        var first = await automation.RunOnceAsync();
        var second = await automation.RunOnceAsync();

        Assert.True(first.NewAlerts >= 1);
        Assert.Equal(0, second.NewAlerts);
        Assert.Equal(1, await _db.CountAsync("DelayAlerts", late));
        Assert.Equal(0, await _db.CountAsync("DelayAlerts", fresh));
        Assert.Equal(10, (int)(await _db.ScalarAsync("SELECT UninspectedAtDetection FROM DelayAlerts WHERE LotId = @id", ("@id", late)))!);
    }

    [Fact(DisplayName = "지연 알림 기록은 고칠 수 없다: 관리자 연결은 트리거가 막고(51000), 앱 계정은 권한이 없다(229)")]
    public async Task DelayAlert_AppendOnly()
    {
        var lot = await fixture.CreateLotAsync(1);
        await _db.ScalarAsync("UPDATE Lots SET InspectionRequestedAt = DATEADD(HOUR, -60, SYSUTCDATETIME()) WHERE Id = @id", ("@id", lot));
        await WithClock(DateTimeOffset.UtcNow).Automation.RunOnceAsync();
        Assert.Equal(1, await _db.CountAsync("DelayAlerts", lot));

        await SqlAssert.ThrowsAsync(51000, () => _db.ScalarAsync("UPDATE DelayAlerts SET UninspectedAtDetection = 0 WHERE LotId = @id", ("@id", lot)));
        await SqlAssert.ThrowsAsync(51000, () => _db.ScalarAsync("DELETE FROM DelayAlerts WHERE LotId = @id", ("@id", lot)));
        await SqlAssert.ThrowsAsync(229, () => _db.ExecuteAsAppAsync($"UPDATE DelayAlerts SET UninspectedAtDetection = 0 WHERE LotId = {lot}"));
        await SqlAssert.ThrowsAsync(229, () => _db.ExecuteAsAppAsync($"DELETE FROM DelayAlerts WHERE LotId = {lot}"));
    }

    [Theory(DisplayName = "보고서·알림 화면은 관리자만")]
    [InlineData("admin", 200)]
    [InlineData("producer", 403)]
    [InlineData("inspector_approver", 403)]
    public async Task ReportsPage_OnlyAdmin(string user, int status)
    {
        Assert.Equal(status, (int)(await _factory.ClientFor(user).GetAsync("/Admin/Reports")).StatusCode);
    }

    [Fact(DisplayName = "보고서 내려받기: 만든 파일은 받을 수 있고, 형식에 맞지 않는 이름(경로 조작)은 404")]
    public async Task Download()
    {
        var date = Kst.Today.AddDays(-5);
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DailyReport>().GenerateAsync(date);
        File.WriteAllText(Path.Combine(_factory.ReportsPath, "secret.txt"), "x");
        var client = _factory.ClientFor("admin");

        var ok = await client.GetAsync($"/Admin/Reports/Download?name=qc-daily-{date:yyyyMMdd}.xlsx");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ok.Content.Headers.ContentType!.MediaType);
        var bytes = await ok.Content.ReadAsByteArrayAsync();
        Assert.Equal((byte)'P', bytes[0]);   // xlsx는 zip

        foreach (var name in new[] { "secret.txt", "../appsettings.json", "qc-daily-20990101.xlsx", "qc-daily-2026101.xlsx" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Admin/Reports/Download?name={Uri.EscapeDataString(name)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("producer")
            .GetAsync($"/Admin/Reports/Download?name=qc-daily-{date:yyyyMMdd}.xlsx")).StatusCode);
    }

    [Fact(DisplayName = "오늘 이후 날짜로 보고서를 만들려 하면 오류(빨간 안내)로 거절하고 파일을 만들지 않는다")]
    public async Task Generate_FutureDate_Rejected()
    {
        var client = _factory.ClientFor("admin");
        var page = await client.GetStringAsync("/Admin/Reports");
        var token = System.Text.RegularExpressions.Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        var response = await client.PostAsync("/Admin/Reports/Generate", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["BusinessDate"] = Kst.Today.ToString("yyyy-MM-dd"),
            ["__RequestVerificationToken"] = token,
        }));
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("alert-danger", html);
        Assert.Contains("어제 이전", html);
        Assert.DoesNotContain("alert-success", html);
        Assert.Empty(Directory.GetFiles(_factory.ReportsPath, "*.xlsx"));
    }
}
