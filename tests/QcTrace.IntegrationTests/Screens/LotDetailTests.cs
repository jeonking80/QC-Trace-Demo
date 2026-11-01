using System.Net;
using System.Text.Json;
using QcTrace.IntegrationTests.Infrastructure;

namespace QcTrace.IntegrationTests.Screens;

/// <summary>LOT 상세·조회(집 PC 피드백 B1·B2·B3·B6)</summary>
[Collection(SqlServerCollection.Name)]
public sealed class LotDetailTests(SqlServerFixture fixture) : IDisposable
{
    private readonly ApiFactory _factory = new(fixture);
    private readonly Db _db = new(fixture);

    public void Dispose() => _factory.Dispose();

    private async Task<string> LotNoAsync(int lot) => (string)(await _db.ScalarAsync("SELECT LotNo FROM Lots WHERE Id = @id", ("@id", lot)))!;

    [Theory(DisplayName = "LOT 상세는 역할이 있으면 누구나 보고, 역할 없는 계정은 403, 없는 LOT은 404")]
    [InlineData("producer", 200)]
    [InlineData("inspector1", 200)]
    [InlineData("approver", 200)]
    [InlineData("shipper", 200)]
    [InlineData("admin", 200)]
    [InlineData("norole", 403)]
    public async Task Access(string user, int status)
    {
        var lotNo = await LotNoAsync(await fixture.CreateLotAsync(5));
        var client = _factory.ClientFor(user);
        Assert.Equal(status, (int)(await client.GetAsync($"/Trace/Lot/{lotNo}")).StatusCode);
        Assert.Equal(status, (int)(await client.GetAsync("/Trace")).StatusCode);
        if (status == 200) Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Trace/Lot/NO-SUCH-LOT")).StatusCode);
    }

    [Fact(DisplayName = "생산 담당도 LOT 상세에서 불합격 사유·검사자·측정값과 폐기 처리, 다음 할 일을 본다")]
    public async Task Producer_SeesReasons()
    {
        var (itemId, diameterId, lengthId) = await fixture.CreateItemWithSpecsAsync();
        var lot = await fixture.CreateLotAsync(10, itemId: itemId);
        var lotNo = await LotNoAsync(lot);
        var measured = JsonSerializer.Serialize(new[]
        {
            new { inspectionItemId = diameterId, name = "외경", value = 142.5m },
            new { inspectionItemId = lengthId, name = "길이", value = 50m },
        });
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 4, "Fail", reason: "외경 상한 초과 흠집", measuredJson: measured);
        await _db.DisposeFailed(Db.Key(), lot, "admin", 3, "Scrap", "용접부 균열로 폐기");

        var html = await _factory.ClientFor("producer").GetStringAsync($"/Trace/Lot/{lotNo}");

        Assert.Contains("외경 상한 초과 흠집", html);
        Assert.Contains("142.5", html);
        Assert.Contains("규격 밖", html);
        Assert.Contains("불합격 → 폐기 3개", html);
        Assert.Contains("용접부 균열로 폐기", html);
        Assert.Contains("남은 미검사 6개를 검사합니다", html);
        Assert.Contains("불합격 1개를 재검사 요청합니다", html);
        Assert.DoesNotContain("처리하러 가기", html);   // 생산 담당에게는 검사 화면 링크가 없다

        var list = await _factory.ClientFor("producer").GetStringAsync("/Lots");
        Assert.Contains("최근 불합격 사유: 외경 상한 초과 흠집", list);
    }

    [Fact(DisplayName = "검사자 화면 '최근 처리 내역'에 폐기·재검사가 남고, '내가 한 것'은 내 작업만")]
    public async Task Inspector_SeesRecentWork()
    {
        var lot = await fixture.CreateLotAsync(10);
        var lotNo = await LotNoAsync(lot);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 10, "Fail", reason: "불량");
        await _db.DisposeFailed(Db.Key(), lot, "admin", 6, "Scrap", "최근내역 폐기 확인");
        await _db.DisposeFailed(Db.Key(), lot, "inspector1", 4, "Reinspect", "최근내역 재검사 확인");

        var all = await _factory.ClientFor("inspector1").GetStringAsync("/Inspections");
        Assert.Contains("최근내역 폐기 확인", all);
        Assert.Contains("최근내역 재검사 확인", all);
        var mine = await _factory.ClientFor("inspector1").GetStringAsync("/Inspections?mine=true");
        Assert.DoesNotContain("최근내역 폐기 확인", mine);
        Assert.Contains("최근내역 재검사 확인", mine);
        Assert.Contains(lotNo, mine);
    }

    [Fact(DisplayName = "대시보드 불합격 처리 카드: 처리 대기 불합격 수량과 오늘 폐기·재검사 수량이 DB와 같다")]
    public async Task Dashboard_FailedHandling()
    {
        var lot = await fixture.CreateLotAsync(10);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 10, "Fail", reason: "불량");
        await _db.DisposeFailed(Db.Key(), lot, "admin", 3, "Scrap", "폐기");
        await _db.DisposeFailed(Db.Key(), lot, "inspector1", 2, "Reinspect", "재검사");

        var json = JsonDocument.Parse(await _factory.ClientFor("admin").GetStringAsync("/api/dashboard")).RootElement.GetProperty("failedHandling");
        var pending = (int)(await _db.ScalarAsync("SELECT SUM(Failed) FROM LotQuantities"))!;
        var since = QcTrace.Web.Services.Kst.StartOfDayUtc(QcTrace.Web.Services.Kst.Today);
        var scrapped = (int)(await _db.ScalarAsync("SELECT ISNULL(SUM(Delta), 0) FROM Ledger WHERE Kind = N'Scrap' AND DeltaField = N'Scrapped' AND CreatedAt >= @since", ("@since", since)))!;
        var reinspected = (int)(await _db.ScalarAsync("SELECT ISNULL(SUM(Delta), 0) FROM Ledger WHERE Kind = N'Reinspect' AND DeltaField = N'Uninspected' AND Delta > 0 AND CreatedAt >= @since", ("@since", since)))!;
        Assert.Equal(pending, json.GetProperty("pendingQty").GetInt32());
        Assert.Equal(scrapped, json.GetProperty("scrappedToday").GetInt32());
        Assert.Equal(reinspected, json.GetProperty("reinspectedToday").GetInt32());
        Assert.True(scrapped >= 3 && reinspected >= 2);
    }
}
