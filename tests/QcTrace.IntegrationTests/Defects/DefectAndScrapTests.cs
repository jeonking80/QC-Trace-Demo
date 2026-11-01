using System.Net;
using QcTrace.IntegrationTests.Infrastructure;

namespace QcTrace.IntegrationTests.Defects;

/// <summary>
/// 2026-11-01 사용자 결정: 불량 사유는 목록(불량 유형)에서 고르고 "기타"면 직접 입력, 폐기는 관리자만.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class DefectAndScrapTests(SqlServerFixture fixture) : IDisposable
{
    private readonly Db _db = new(fixture);
    private readonly ApiFactory _factory = new(fixture);
    public void Dispose() => _factory.Dispose();

    private async Task<int> CodeAsync(string code) =>
        (int)(await _db.ScalarAsync("SELECT Id FROM DefectCodes WHERE Code = @c", ("@c", code)))!;

    private async Task<(string? Reason, int? Code)> LastInspectionAsync(int lot)
    {
        var reason = await _db.ScalarAsync("SELECT TOP 1 Reason FROM Inspections WHERE LotId = @id ORDER BY Id DESC", ("@id", lot)) as string;
        var code = await _db.ScalarAsync("SELECT TOP 1 DefectCodeId FROM Inspections WHERE LotId = @id ORDER BY Id DESC", ("@id", lot)) as int?;
        return (reason, code);
    }

    [Fact(DisplayName = "불량 유형: 유형을 고르면 유형 이름이 사유가 되고(설명은 덧붙음), 검사 기록에 유형이 남는다")]
    public async Task Code_BecomesReason()
    {
        var dim = await CodeAsync("DIM-OVER");
        var lot = await fixture.CreateLotAsync(10);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 2, "Fail", defectCodeId: dim);
        Assert.Equal(("치수 상한 초과", dim), await LastInspectionAsync(lot));
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 1, "Hold", reason: "외경 141.3", defectCodeId: dim);
        Assert.Equal(("치수 상한 초과 - 외경 141.3", (int?)dim), await LastInspectionAsync(lot));
        Assert.Equal("치수 상한 초과 - 외경 141.3",
            await _db.ScalarAsync("SELECT TOP 1 Reason FROM Ledger WHERE LotId = @id AND Kind = N'Inspect' ORDER BY Id DESC", ("@id", lot)));
    }

    [Fact(DisplayName = "불량 유형: 기타는 사유 필수(52004), 합격에 유형 50400, 사용 중지 유형 50400, 유형 없이 사유만도 그대로 받는다")]
    public async Task Code_Rules()
    {
        var etc = await CodeAsync("ETC");
        var lot = await fixture.CreateLotAsync(10);
        await SqlAssert.ThrowsAsync(52004, () => _db.ApplyInspection(Db.Key(), lot, "inspector1", 1, "Fail", defectCodeId: etc));
        await SqlAssert.ThrowsAsync(50400, () => _db.ApplyInspection(Db.Key(), lot, "inspector1", 1, "Pass", defectCodeId: etc));
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 1, "Fail", reason: "도장 들뜸", defectCodeId: etc);
        Assert.Equal(("도장 들뜸", (int?)etc), await LastInspectionAsync(lot));

        var off = (int)(await _db.ScalarAsync("INSERT INTO DefectCodes (Code, Name, IsActive, SortOrder) OUTPUT INSERTED.Id VALUES (@c, N'중지된 유형', 0, 500)",
            ("@c", "OFF-" + Guid.NewGuid().ToString("N")[..8])))!;
        await SqlAssert.ThrowsAsync(50400, () => _db.ApplyInspection(Db.Key(), lot, "inspector1", 1, "Fail", defectCodeId: off));

        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 1, "Fail", reason: "API 사유만");   // 기존 호출
        Assert.Equal(("API 사유만", (int?)null), await LastInspectionAsync(lot));
        Assert.Equal(new Quantities(8, 0, 2, 0, 0, 0), await _db.QuantitiesAsync(lot));   // 성공한 불합격 2건만 반영
    }

    [Fact(DisplayName = "불량 유형: 처리된 요청은 그 유형을 나중에 중지해도 같은 키 재전송이 저장된 응답으로 돌아온다(R6)")]
    public async Task Replay_AfterCodeDeactivated()
    {
        var code = (int)(await _db.ScalarAsync("INSERT INTO DefectCodes (Code, Name, IsActive, SortOrder) OUTPUT INSERTED.Id VALUES (@c, N'곧 중지할 유형', 1, 500)",
            ("@c", "TMP-" + Guid.NewGuid().ToString("N")[..8])))!;
        var lot = await fixture.CreateLotAsync(10);
        var key = Db.Key();
        await _db.ApplyInspection(key, lot, "inspector1", 2, "Fail", defectCodeId: code);
        await _db.ScalarAsync("UPDATE DefectCodes SET IsActive = 0 WHERE Id = @id", ("@id", code));
        await _db.ApplyInspection(key, lot, "inspector1", 2, "Fail", defectCodeId: code);   // replay, 오류 없음
        Assert.Equal(new Quantities(8, 0, 2, 0, 0, 0), await _db.QuantitiesAsync(lot));
        await SqlAssert.ThrowsAsync(50400, () => _db.ApplyInspection(Db.Key(), lot, "inspector1", 1, "Fail", defectCodeId: code));
    }

    [Fact(DisplayName = "검사 화면: 불합격·보류는 불량 유형을 골라야 하고 목록이 보인다. 관리자는 불량 유형을 추가·중지하고 유형별 수량을 본다")]
    public async Task Screens()
    {
        var lot = await fixture.CreateLotAsync(5);
        var input = await _factory.ClientFor("inspector1").GetStringAsync($"/Inspections/Input/{lot}");
        Assert.Contains("기타 (직접 입력)", input);
        Assert.Contains("치수 상한 초과", input);

        var admin = _factory.ClientFor("admin");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Admin/DefectCodes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("inspector1").GetAsync("/Admin/DefectCodes")).StatusCode);
    }

    [Fact(DisplayName = "폐기는 관리자만: 검사자 폐기 50403, 관리자 재검사 요청 50403, 관리자 폐기는 원장에 관리자로 남는다")]
    public async Task Scrap_AdminOnly()
    {
        var lot = await fixture.CreateLotAsync(10);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 6, "Fail", reason: "불량");
        await SqlAssert.ThrowsAsync(50403, () => _db.DisposeFailed(Db.Key(), lot, "inspector1", 1, "Scrap", "폐기"));
        await SqlAssert.ThrowsAsync(50403, () => _db.DisposeFailed(Db.Key(), lot, "admin", 1, "Reinspect", "재검사"));
        await _db.DisposeFailed(Db.Key(), lot, "inspector2", 2, "Reinspect", "재측정");
        await _db.DisposeFailed(Db.Key(), lot, "admin", 4, "Scrap", "수리 불가");
        Assert.Equal(new Quantities(6, 0, 0, 0, 0, 0, 4), await _db.QuantitiesAsync(lot));
        Assert.Equal(_db.User("admin"), await _db.ScalarAsync("SELECT TOP 1 ActorId FROM Ledger WHERE LotId = @id AND Kind = N'Scrap'", ("@id", lot)));
        await _db.AssertLedgerMatchesAsync(lot);
    }

    [Fact(DisplayName = "폐기 화면: 관리자만 '불합격 폐기'에 들어가고, 검사자 불합격 처리 화면에는 폐기 선택이 없다. API도 검사자 폐기는 403")]
    public async Task Scrap_Screens()
    {
        var lot = await fixture.CreateLotAsync(10);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 3, "Fail", reason: "폐기 화면 확인용 불량");
        Assert.Contains("폐기 화면 확인용 불량", await _factory.ClientFor("admin").GetStringAsync("/Admin/Scrap"));
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("inspector1").GetAsync("/Admin/Scrap")).StatusCode);
        var dispose = await _factory.ClientFor("inspector1").GetStringAsync($"/Inspections/Dispose/{lot}");
        Assert.DoesNotContain("value=\"Scrap\"", dispose);
        Assert.Contains("폐기는 관리자만", dispose);

        var api = _factory.ClientFor("inspector1");
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/lots/{lot}/dispositions")
        { Content = System.Net.Http.Json.JsonContent.Create(new { qty = 1, action = "Scrap", reason = "폐기" }) };
        req.Headers.Add("Idempotency-Key", Db.Key());
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(req)).StatusCode);
    }
}
