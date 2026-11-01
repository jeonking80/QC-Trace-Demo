using System.Net;
using System.Text.Json;
using QcTrace.IntegrationTests.Infrastructure;

namespace QcTrace.IntegrationTests.Api;

/// <summary>/api/dashboard: 관리자 전용, 수치는 DB를 독립 쿼리로 다시 센 값과 같아야 한다.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class DashboardApiTests(SqlServerFixture fixture) : IDisposable
{
    private readonly ApiFactory _factory = new(fixture);
    private readonly Db _db = new(fixture);

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> GetAsync()
    {
        var response = await _factory.ClientFor("admin").GetAsync("/api/dashboard");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Theory(DisplayName = "대시보드 API는 관리자만: 다른 역할 403, 로그인 안 하면 401")]
    [InlineData("producer", 403)]
    [InlineData("inspector_approver", 403)]
    [InlineData("norole", 403)]
    [InlineData(null, 401)]
    public async Task OnlyAdmin(string? user, int status)
    {
        var client = user is null ? _factory.CreateClient() : _factory.ClientFor(user);
        Assert.Equal(status, (int)(await client.GetAsync("/api/dashboard")).StatusCode);
    }

    [Fact(DisplayName = "48시간 넘게 미검사로 남은 LOT은 지연으로 잡히고 조치 목록에 검사 지연으로 나온다")]
    public async Task DelayedLot_Appears()
    {
        var lot = await fixture.CreateLotAsync(10);
        var fresh = await fixture.CreateLotAsync(10);
        await _db.ScalarAsync("UPDATE Lots SET InspectionRequestedAt = DATEADD(HOUR, -49, SYSUTCDATETIME()) WHERE Id = @id", ("@id", lot));
        var lotNo = (string)(await _db.ScalarAsync("SELECT LotNo FROM Lots WHERE Id = @id", ("@id", lot)))!;
        var freshNo = (string)(await _db.ScalarAsync("SELECT LotNo FROM Lots WHERE Id = @id", ("@id", fresh)))!;

        var d = await GetAsync();

        var delayed = d.GetProperty("delayed").GetProperty("lots").EnumerateArray().Select(x => x.GetProperty("lotNo").GetString()).ToList();
        Assert.Contains(lotNo, delayed);
        Assert.DoesNotContain(freshNo, delayed);
        var action = d.GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("kind").GetString() == "delayed" && a.GetProperty("lotNo").GetString() == lotNo);
        Assert.Equal("danger", action.GetProperty("severity").GetString());
        // 관리자에게 검사자 역할이 없으므로 그 LOT 이력 원장으로 간다
        Assert.StartsWith("/Admin/Ledger?LotNo=", action.GetProperty("href").GetString());

        var expectedDelayed = (int)(await _db.ScalarAsync("""
            SELECT COUNT(*) FROM Lots l JOIN LotQuantities q ON q.LotId = l.Id
            WHERE l.Status IN (N'Waiting', N'Inspecting') AND q.Uninspected > 0 AND l.InspectionRequestedAt < DATEADD(HOUR, -48, SYSUTCDATETIME())
            """))!;
        Assert.Equal(expectedDelayed, d.GetProperty("delayed").GetProperty("count").GetInt32());
    }

    [Fact(DisplayName = "검사 대기·오늘 불합격률·당일 출하는 원본 테이블을 직접 센 값과 같다")]
    public async Task Kpis_MatchIndependentQueries()
    {
        var lot = await fixture.CreateLotAsync(20);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 8, "Pass");
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 2, "Fail", reason: "불량");
        await _db.ApproveShipment(Db.Key(), lot, "approver", 5);
        await _db.ShipLot(Db.Key(), lot, "shipper", 3);

        var d = await GetAsync();

        const string kstToday = "CAST(DATEADD(HOUR, 9, SYSUTCDATETIME()) AS date)";
        var inspected = (int)(await _db.ScalarAsync($"SELECT ISNULL(SUM(Qty), 0) FROM Inspections WHERE CorrectsInspectionId IS NULL AND CAST(DATEADD(HOUR, 9, CreatedAt) AS date) = {kstToday}"))!;
        var failed = (int)(await _db.ScalarAsync($"SELECT ISNULL(SUM(Qty), 0) FROM Inspections WHERE CorrectsInspectionId IS NULL AND Verdict = N'Fail' AND CAST(DATEADD(HOUR, 9, CreatedAt) AS date) = {kstToday}"))!;
        var shipped = (int)(await _db.ScalarAsync($"SELECT ISNULL(SUM(Qty), 0) FROM Shipments WHERE CAST(DATEADD(HOUR, 9, CreatedAt) AS date) = {kstToday}"))!;
        var waitingLots = (int)(await _db.ScalarAsync("SELECT COUNT(*) FROM Lots l JOIN LotQuantities q ON q.LotId = l.Id WHERE l.Status IN (N'Waiting', N'Inspecting') AND q.Uninspected > 0"))!;

        var today = d.GetProperty("failRate").GetProperty("today");
        Assert.Equal(inspected, today.GetProperty("inspected").GetInt32());
        Assert.Equal(failed, today.GetProperty("failed").GetInt32());
        Assert.Equal(Math.Round((decimal)failed / inspected, 4), today.GetProperty("failRate").GetDecimal());
        Assert.Equal(shipped, d.GetProperty("shippedToday").GetProperty("qty").GetInt32());
        Assert.Equal(waitingLots, d.GetProperty("waiting").GetProperty("lots").GetInt32());
        Assert.Equal(7, d.GetProperty("failRate").GetProperty("daily").GetArrayLength());

        var lotNo = (string)(await _db.ScalarAsync("SELECT LotNo FROM Lots WHERE Id = @id", ("@id", lot)))!;
        var kinds = d.GetProperty("actions").EnumerateArray().Where(a => a.GetProperty("lotNo").GetString() == lotNo)
            .Select(a => a.GetProperty("kind").GetString()).ToHashSet();
        Assert.Superset(new HashSet<string?> { "failed", "ship" }, kinds); // 불합격 2개 처리 대기, 예약 2개 출하 대기
    }

    [Fact(DisplayName = "대사 불일치 LOT은 조치 목록 맨 앞에 재고 대사 불일치로 나온다")]
    public async Task Mismatch_FirstInActions()
    {
        var lot = await fixture.CreateLotAsync(10);
        await _db.ScalarAsync("UPDATE LotQuantities SET Uninspected = 9 WHERE LotId = @id", ("@id", lot));

        var d = await GetAsync();

        var first = d.GetProperty("actions")[0];
        Assert.Equal("mismatch", first.GetProperty("kind").GetString());
        Assert.Equal("/Admin/Reconcile", first.GetProperty("href").GetString());
        Assert.True(d.GetProperty("reconciliation").GetProperty("mismatches").GetInt32() >= 1);
    }
}
