using QcTrace.RuleTests.Infrastructure;

namespace QcTrace.RuleTests.Rules;

/// <summary>R1. 검사 완료 전 LOT은 출하 가능 수량에 포함하지 않는다.</summary>
public class R01_UninspectedNotShippableTests(SqlServerFixture fx) : RuleTestBase(fx)
{
    [Fact(DisplayName = "R1 검사 전 LOT 출하 승인 요청은 52001로 거절된다")]
    public async Task Approve_before_inspection_is_rejected()
    {
        var lot = await Db.CreateWaitingLotAsync(100);
        var approver = await Db.CreateUserAsync("Approver");

        await AssertSqlErrorAsync(52001, () => Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 1));

        var q = await Db.GetQuantitiesAsync(lot);
        Assert.Equal(new Quantities(100, 0, 0, 0, 0, 0), q);
        Assert.Equal(0, await Db.CountAsync("ShipmentApprovals", lot));
    }

    [Fact(DisplayName = "R1 검사 전 LOT 출하 요청은 거절되고 출하 수량이 변하지 않는다")]
    public async Task Ship_before_inspection_is_rejected()
    {
        var lot = await Db.CreateWaitingLotAsync(100);
        var shipper = await Db.CreateUserAsync("Shipper");

        // 승인 잔량(예약)이 없으므로 R7 오류 번호로 거절된다
        await AssertSqlErrorAsync(52007, () => Db.ShipLotAsync(TestDb.NewKey(), lot, shipper, 1));

        Assert.Equal(new Quantities(100, 0, 0, 0, 0, 0), await Db.GetQuantitiesAsync(lot));
        Assert.Equal(0, await Db.CountAsync("Shipments", lot));
    }

    [Fact(DisplayName = "R1 부분 검사 LOT은 합격 수량까지만 승인되고 미검사분은 승인 대상이 아니다")]
    public async Task Only_passed_quantity_is_available()
    {
        var (lot, _) = await CreatePassedLotAsync(100, 60);
        var approver = await Db.CreateUserAsync("Approver");

        var before = await Db.GetQuantitiesAsync(lot);
        Assert.Equal(60, before.Available);

        await AssertSqlErrorAsync(52001, () => Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 61));

        var ok = await Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 60);
        Assert.Equal(0, QtyOf(ok, "available"));
        Assert.Equal(60, QtyOf(ok, "reserved"));

        var after = await Db.GetQuantitiesAsync(lot);
        Assert.Equal(40, after.Uninspected);
        Assert.Equal(0, after.Available);
        await AssertLedgerMatchesQuantitiesAsync(lot);
    }

    [Fact(DisplayName = "R1 불합격·보류 수량은 출하 가능 수량에 들어가지 않는다")]
    public async Task Failed_and_held_are_not_available()
    {
        var lot = await Db.CreateWaitingLotAsync(100);
        var inspector = await Db.CreateUserAsync("Inspector");
        var approver = await Db.CreateUserAsync("Approver");

        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 50, Fail, reason: "치수 불량");
        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 50, Hold, reason: "재측정 필요");

        Assert.Equal(0, (await Db.GetQuantitiesAsync(lot)).Available);
        await AssertSqlErrorAsync(52001, () => Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 1));
    }

    [Fact(DisplayName = "R1 승인으로 예약된 수량은 다시 승인할 수 없다")]
    public async Task Reserved_quantity_is_not_available_again()
    {
        var (lot, _) = await CreatePassedLotAsync(100, 100);
        var approver = await Db.CreateUserAsync("Approver");

        await Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 70);
        await AssertSqlErrorAsync(52001, () => Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 31));
        await Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 30);

        var q = await Db.GetQuantitiesAsync(lot);
        Assert.Equal(100, q.Reserved);
        Assert.Equal(0, q.Available);
    }
}
