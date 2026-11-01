using QcTrace.IntegrationTests.Infrastructure;

namespace QcTrace.IntegrationTests.StoredProcedures;

/// <summary>재검사로 미검사가 다시 생기면 LOT은 부분 검사(Inspecting)로 돌아가 검사 목록에 다시 나온다 (단위 10 E2E에서 발견한 문제)</summary>
[Collection(SqlServerCollection.Name)]
public class ReinspectStatusTests(SqlServerFixture fixture)
{
    private readonly Db _db = new(fixture);

    [Fact(DisplayName = "전량 불합격 → 전량 재검사하면 상태가 부분 검사로 돌아가고 다시 검사할 수 있다")]
    public async Task FullFail_FullReinspect_BackToInspecting()
    {
        var lot = await fixture.CreateLotAsync(20);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 20, "Fail", reason: "불량");
        Assert.Equal("Inspected", await _db.StatusAsync(lot));

        await _db.DisposeFailed(Db.Key(), lot, "inspector1", 20, "Reinspect", "재검사");
        Assert.Equal("Inspecting", await _db.StatusAsync(lot));
        Assert.Equal(new Quantities(20, 0, 0, 0, 0, 0), await _db.QuantitiesAsync(lot));

        await _db.ApplyInspection(Db.Key(), lot, "inspector2", 20, "Pass");
        Assert.Equal("Inspected", await _db.StatusAsync(lot));
        await _db.AssertLedgerMatchesAsync(lot);
    }

    [Fact(DisplayName = "합격분을 전량 출하한 뒤 불합격분을 재검사해도 부분 검사로 돌아간다")]
    public async Task Shipped_ThenReinspect_BackToInspecting()
    {
        var lot = await fixture.CreateLotAsync(10);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 7, "Pass");
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 3, "Fail", reason: "불량");
        await _db.ApproveShipment(Db.Key(), lot, "approver", 7);
        await _db.ShipLot(Db.Key(), lot, "shipper", 7);
        Assert.Equal("Shipped", await _db.StatusAsync(lot));

        await _db.DisposeFailed(Db.Key(), lot, "inspector1", 3, "Reinspect", "재검사");
        Assert.Equal("Inspecting", await _db.StatusAsync(lot));
    }
}
