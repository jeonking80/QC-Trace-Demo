using QcTrace.IntegrationTests.Infrastructure;

namespace QcTrace.IntegrationTests.StoredProcedures;

[Collection(SqlServerCollection.Name)]
public class ShipmentTests(SqlServerFixture fixture)
{
    private readonly Db _db = new(fixture);

    [Fact(DisplayName = "R1 검사 전 수량은 출하 승인할 수 없다")]
    public async Task ApproveBeforeInspection_IsRejected()
    {
        var lot = await fixture.CreateLotAsync(10);

        await SqlAssert.ThrowsAsync(52001, () => _db.ApproveShipment(Db.Key(), lot, "approver", 1));

        Assert.Equal(new Quantities(10, 0, 0, 0, 0, 0), await _db.QuantitiesAsync(lot));
    }

    [Fact(DisplayName = "R1 합격 수량을 넘는 승인은 거절, 합격 수량까지는 승인")]
    public async Task ApproveMoreThanPassed_IsRejected()
    {
        var lot = await fixture.CreateLotAsync(100);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 60, "Pass");

        await SqlAssert.ThrowsAsync(52001, () => _db.ApproveShipment(Db.Key(), lot, "approver", 61));
        var ok = await _db.ApproveShipment(Db.Key(), lot, "approver", 60);

        Assert.Equal(0, ok.Response.GetProperty("quantities").GetProperty("available").GetInt32());
        Assert.Equal(new Quantities(40, 60, 0, 0, 0, 60), await _db.QuantitiesAsync(lot));
        await SqlAssert.ThrowsAsync(52001, () => _db.ApproveShipment(Db.Key(), lot, "approver", 1));
        await _db.AssertLedgerMatchesAsync(lot);
    }

    [Fact(DisplayName = "R5 검사자 본인은 자기 LOT을 승인할 수 없고, 다른 승인권자는 승인할 수 있다")]
    public async Task InspectorCannotApproveOwnLot()
    {
        var lot = await fixture.CreateLotAsync(20);
        await _db.ApplyInspection(Db.Key(), lot, "inspector_approver", 20, "Pass");

        await SqlAssert.ThrowsAsync(52005, () => _db.ApproveShipment(Db.Key(), lot, "inspector_approver", 20));
        await _db.ApproveShipment(Db.Key(), lot, "approver", 20);

        Assert.Equal(20, (await _db.QuantitiesAsync(lot)).Reserved);
        Assert.Equal(1, await _db.CountAsync("ShipmentApprovals", lot));
    }

    [Fact(DisplayName = "R5 다른 사람이 검사한 LOT은 검사자 겸 승인권자도 승인할 수 있다")]
    public async Task InspectorApproverCanApproveOthersLot()
    {
        var lot = await fixture.CreateLotAsync(20);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 20, "Pass");

        await _db.ApproveShipment(Db.Key(), lot, "inspector_approver", 20);

        Assert.Equal(20, (await _db.QuantitiesAsync(lot)).Reserved);
    }

    [Fact(DisplayName = "반려는 사유가 필요하고, 수량을 바꾸지 않는다")]
    public async Task Reject_RequiresReason_AndKeepsQuantities()
    {
        var lot = await fixture.CreateLotAsync(10);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 10, "Pass");

        await SqlAssert.ThrowsAsync(52004, () => _db.ApproveShipment(Db.Key(), lot, "approver", 0, "Rejected"));
        await _db.ApproveShipment(Db.Key(), lot, "approver", 0, "Rejected", "포장 상태 확인 필요");

        Assert.Equal(new Quantities(0, 10, 0, 0, 0, 0), await _db.QuantitiesAsync(lot));
        Assert.Equal(1, await _db.CountAsync("ShipmentApprovals", lot));
    }

    [Fact(DisplayName = "R7 승인 잔량을 넘는 출하는 거절, 승인 수량 안에서는 출하")]
    public async Task ShipMoreThanApproved_IsRejected()
    {
        var lot = await fixture.CreateLotAsync(100);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 95, "Pass");
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 5, "Fail", reason: "외경 상한 초과");
        await _db.ApproveShipment(Db.Key(), lot, "approver", 95);

        await SqlAssert.ThrowsAsync(52007, () => _db.ShipLot(Db.Key(), lot, "shipper", 100));
        var shipped = await _db.ShipLot(Db.Key(), lot, "shipper", 95);

        Assert.Equal(new Quantities(0, 95, 5, 0, 95, 0), await _db.QuantitiesAsync(lot));
        // 불합격 5가 처리 전이라 출하 완료가 아니라 검사 완료로 남는다(집 PC 피드백 A2)
        Assert.Equal("Inspected", shipped.Response.GetProperty("status").GetString());
        Assert.Equal("Inspected", await _db.StatusAsync(lot));
        await _db.AssertLedgerMatchesAsync(lot);
    }

    [Fact(DisplayName = "R7 승인 전에는 합격 수량이 있어도 출하할 수 없다")]
    public async Task ShipBeforeApproval_IsRejected()
    {
        var lot = await fixture.CreateLotAsync(10);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 10, "Pass");

        await SqlAssert.ThrowsAsync(52007, () => _db.ShipLot(Db.Key(), lot, "shipper", 1));
    }

    [Fact(DisplayName = "R6 출하 재전송은 한 번만 반영")]
    public async Task ShipResent_AppliesOnce()
    {
        var lot = await fixture.CreateLotAsync(10);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 10, "Pass");
        await _db.ApproveShipment(Db.Key(), lot, "approver", 10);
        var key = Db.Key();

        await _db.ShipLot(key, lot, "shipper", 4);
        var again = await _db.ShipLot(key, lot, "shipper", 4);

        Assert.True(again.Replayed);
        Assert.Equal(new Quantities(0, 10, 0, 0, 4, 6), await _db.QuantitiesAsync(lot));
        Assert.Equal(1, await _db.CountAsync("Shipments", lot));
    }

    [Fact(DisplayName = "출하 담당 역할이 없으면 출하할 수 없다")]
    public async Task NonShipper_IsRejected()
    {
        var lot = await fixture.CreateLotAsync(10);
        await SqlAssert.ThrowsAsync(50403, () => _db.ShipLot(Db.Key(), lot, "approver", 1));
    }
}
