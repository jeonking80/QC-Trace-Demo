using QcTrace.RuleTests.Infrastructure;

namespace QcTrace.RuleTests.Rules;

/// <summary>R7. 출하 수량은 승인된 합격 수량을 넘을 수 없다.</summary>
public class R07_ShipWithinApprovedTests(SqlServerFixture fx) : RuleTestBase(fx)
{
    private async Task<(int Lot, string Shipper)> ApprovedLotAsync(int produced, int passed, int approved)
    {
        var (lot, _) = await CreatePassedLotAsync(produced, passed);
        var approver = await Db.CreateUserAsync("Approver");
        await Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, approved);
        return (lot, await Db.CreateUserAsync("Shipper"));
    }

    [Fact(DisplayName = "R7 승인 수량을 넘는 출하는 52007로 거절된다")]
    public async Task Ship_over_approved_is_rejected()
    {
        var (lot, shipper) = await ApprovedLotAsync(100, 100, 30);

        await AssertSqlErrorAsync(52007, () => Db.ShipLotAsync(TestDb.NewKey(), lot, shipper, 31));

        var q = await Db.GetQuantitiesAsync(lot);
        Assert.Equal(0, q.Shipped);
        Assert.Equal(30, q.Reserved);
        Assert.Equal(0, await Db.CountAsync("Shipments", lot));
    }

    [Fact(DisplayName = "R7 승인 수량만큼 출하하면 예약이 줄고 출하가 늘며 재고가 음수가 되지 않는다")]
    public async Task Ship_exact_approved_moves_reserved_to_shipped()
    {
        var (lot, shipper) = await ApprovedLotAsync(100, 100, 30);

        var r = await Db.ShipLotAsync(TestDb.NewKey(), lot, shipper, 30);
        Assert.Equal(30, r.Json.GetProperty("shippedQty").GetInt32());

        var q = await Db.GetQuantitiesAsync(lot);
        Assert.Equal(30, q.Shipped);
        Assert.Equal(0, q.Reserved);
        Assert.Equal(70, q.Available);
        await AssertLedgerMatchesQuantitiesAsync(lot);

        await AssertSqlErrorAsync(52007, () => Db.ShipLotAsync(TestDb.NewKey(), lot, shipper, 1));
    }

    [Fact(DisplayName = "R7 나눠 출하해도 합계가 승인 수량을 넘을 수 없다")]
    public async Task Split_shipments_limited_by_approved_total()
    {
        var (lot, shipper) = await ApprovedLotAsync(100, 100, 30);

        await Db.ShipLotAsync(TestDb.NewKey(), lot, shipper, 10);
        await Db.ShipLotAsync(TestDb.NewKey(), lot, shipper, 15);
        await AssertSqlErrorAsync(52007, () => Db.ShipLotAsync(TestDb.NewKey(), lot, shipper, 6));
        await Db.ShipLotAsync(TestDb.NewKey(), lot, shipper, 5);

        var q = await Db.GetQuantitiesAsync(lot);
        Assert.Equal(30, q.Shipped);
        Assert.Equal(0, q.Reserved);
        Assert.Equal(3, await Db.CountAsync("Shipments", lot));
    }

    [Fact(DisplayName = "R7 합격했지만 승인되지 않은 수량은 출하할 수 없다")]
    public async Task Passed_but_unapproved_cannot_ship()
    {
        var (lot, _) = await CreatePassedLotAsync(100, 100);
        var shipper = await Db.CreateUserAsync("Shipper");

        await AssertSqlErrorAsync(52007, () => Db.ShipLotAsync(TestDb.NewKey(), lot, shipper, 1));
    }

    [Fact(DisplayName = "R7 전량 출하하면 LOT 상태가 Shipped가 된다")]
    public async Task Full_shipment_sets_status_shipped()
    {
        var (lot, shipper) = await ApprovedLotAsync(20, 20, 20);
        var r = await Db.ShipLotAsync(TestDb.NewKey(), lot, shipper, 20);

        Assert.Equal("Shipped", await Db.GetLotStatusAsync(lot));
        Assert.Equal("Shipped", r.Json.GetProperty("status").GetString());
    }

    [Fact(DisplayName = "R7 출하 역할이 없는 사용자의 출하는 50403으로 거절된다")]
    public async Task Ship_requires_shipper_role()
    {
        var (lot, _) = await ApprovedLotAsync(10, 10, 10);
        var notShipper = await Db.CreateUserAsync("Approver");
        await AssertSqlErrorAsync(50403, () => Db.ShipLotAsync(TestDb.NewKey(), lot, notShipper, 1));
    }

    [Theory(DisplayName = "R7 출하 수량 0 이하는 50400으로 거절된다")]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Non_positive_ship_qty_is_rejected(int qty)
    {
        var (lot, shipper) = await ApprovedLotAsync(10, 10, 10);
        await AssertSqlErrorAsync(50400, () => Db.ShipLotAsync(TestDb.NewKey(), lot, shipper, qty));
        Assert.Equal(10, (await Db.GetQuantitiesAsync(lot)).Reserved);
    }
}
