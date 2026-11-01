using QcTrace.RuleTests.Infrastructure;

namespace QcTrace.RuleTests.Rules;

/// <summary>R6. 같은 요청 키로 재전송된 실적은 한 번만 반영한다.</summary>
public class R06_IdempotencyTests(SqlServerFixture fx) : RuleTestBase(fx)
{
    [Fact(DisplayName = "R6 같은 키·같은 본문 검사 2회 전송 시 수량은 1회만 반영되고 같은 응답을 돌려준다")]
    public async Task Inspection_resend_applied_once()
    {
        var lot = await Db.CreateWaitingLotAsync(100);
        var inspector = await Db.CreateUserAsync("Inspector");
        var key = TestDb.NewKey();

        var first = await Db.ApplyInspectionAsync(key, lot, inspector, 30, Pass);
        var ledgerAfterFirst = await Db.GetLedgerAsync(lot);
        var second = await Db.ApplyInspectionAsync(key, lot, inspector, 30, Pass);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.RawJson, second.RawJson);
        Assert.Equal(new Quantities(70, 30, 0, 0, 0, 0), await Db.GetQuantitiesAsync(lot));
        Assert.Equal(1, await Db.CountAsync("Inspections", lot));
        Assert.Equal(ledgerAfterFirst, await Db.GetLedgerAsync(lot));
        Assert.Equal(1, await Db.CountRequestsAsync(key));
    }

    [Fact(DisplayName = "R6 같은 키·같은 본문 승인 재전송은 예약을 한 번만 늘린다")]
    public async Task Approval_resend_applied_once()
    {
        var (lot, _) = await CreatePassedLotAsync(100, 100);
        var approver = await Db.CreateUserAsync("Approver");
        var key = TestDb.NewKey();

        var first = await Db.ApproveShipmentAsync(key, lot, approver, 40);
        var second = await Db.ApproveShipmentAsync(key, lot, approver, 40);

        Assert.True(second.Replayed);
        Assert.Equal(first.RawJson, second.RawJson);
        Assert.Equal(40, (await Db.GetQuantitiesAsync(lot)).Reserved);
        Assert.Equal(1, await Db.CountAsync("ShipmentApprovals", lot));
    }

    [Fact(DisplayName = "R6 같은 키·같은 본문 출하 재전송은 출하를 한 번만 반영한다")]
    public async Task Shipment_resend_applied_once()
    {
        var (lot, _) = await CreatePassedLotAsync(100, 100);
        var approver = await Db.CreateUserAsync("Approver");
        var shipper = await Db.CreateUserAsync("Shipper");
        await Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 50);
        var key = TestDb.NewKey();

        var first = await Db.ShipLotAsync(key, lot, shipper, 20);
        var second = await Db.ShipLotAsync(key, lot, shipper, 20);

        Assert.True(second.Replayed);
        Assert.Equal(first.RawJson, second.RawJson);
        var q = await Db.GetQuantitiesAsync(lot);
        Assert.Equal(20, q.Shipped);
        Assert.Equal(30, q.Reserved);
        Assert.Equal(1, await Db.CountAsync("Shipments", lot));
    }

    [Fact(DisplayName = "R6 재전송 응답은 그 사이 수량이 바뀌어도 처음 저장된 응답 그대로다")]
    public async Task Replay_returns_stored_response_not_current_state()
    {
        var lot = await Db.CreateWaitingLotAsync(100);
        var inspector = await Db.CreateUserAsync("Inspector");
        var key = TestDb.NewKey();

        var first = await Db.ApplyInspectionAsync(key, lot, inspector, 10, Pass);
        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 10, Pass);
        var replay = await Db.ApplyInspectionAsync(key, lot, inspector, 10, Pass);

        Assert.True(replay.Replayed);
        Assert.Equal(first.RawJson, replay.RawJson);
        Assert.Equal(10, QtyOf(replay, "passed"));
        Assert.Equal(20, (await Db.GetQuantitiesAsync(lot)).Passed);
    }

    [Theory(DisplayName = "R6 같은 키에 다른 본문은 50409로 거절되고 수량이 변하지 않는다")]
    [InlineData(31, "Pass", null)]
    [InlineData(30, "Fail", "불량")]
    [InlineData(30, "Pass", "사유만 다름")]
    public async Task Same_key_different_body_is_conflict(int qty, string verdict, string? reason)
    {
        var lot = await Db.CreateWaitingLotAsync(100);
        var inspector = await Db.CreateUserAsync("Inspector");
        var key = TestDb.NewKey();
        await Db.ApplyInspectionAsync(key, lot, inspector, 30, Pass);

        await AssertSqlErrorAsync(50409, () => Db.ApplyInspectionAsync(key, lot, inspector, qty, verdict, reason: reason));
        Assert.Equal(new Quantities(70, 30, 0, 0, 0, 0), await Db.GetQuantitiesAsync(lot));
    }

    [Fact(DisplayName = "R6 같은 키를 다른 LOT에 쓰면 50409로 거절된다")]
    public async Task Same_key_other_lot_is_conflict()
    {
        var lotA = await Db.CreateWaitingLotAsync(10);
        var lotB = await Db.CreateWaitingLotAsync(10);
        var inspector = await Db.CreateUserAsync("Inspector");
        var key = TestDb.NewKey();
        await Db.ApplyInspectionAsync(key, lotA, inspector, 5, Pass);

        await AssertSqlErrorAsync(50409, () => Db.ApplyInspectionAsync(key, lotB, inspector, 5, Pass));
        Assert.Equal(new Quantities(10, 0, 0, 0, 0, 0), await Db.GetQuantitiesAsync(lotB));
    }

    [Fact(DisplayName = "R6 검사에 쓴 키를 승인에 다시 쓰면 50409로 거절된다")]
    public async Task Same_key_other_operation_is_conflict()
    {
        var lot = await Db.CreateWaitingLotAsync(10);
        var inspector = await Db.CreateUserAsync("Inspector");
        var approver = await Db.CreateUserAsync("Approver");
        var key = TestDb.NewKey();
        await Db.ApplyInspectionAsync(key, lot, inspector, 10, Pass);

        await AssertSqlErrorAsync(50409, () => Db.ApproveShipmentAsync(key, lot, approver, 5));
        Assert.Equal(0, (await Db.GetQuantitiesAsync(lot)).Reserved);
    }

    [Fact(DisplayName = "R6 거절된 요청의 키는 저장되지 않아 올바른 본문으로 다시 쓸 수 있다")]
    public async Task Rejected_request_key_is_not_consumed()
    {
        var lot = await Db.CreateWaitingLotAsync(10);
        var inspector = await Db.CreateUserAsync("Inspector");
        var key = TestDb.NewKey();

        await AssertSqlErrorAsync(52004, () => Db.ApplyInspectionAsync(key, lot, inspector, 2, Fail));
        var ok = await Db.ApplyInspectionAsync(key, lot, inspector, 2, Fail, reason: "사유 보완");

        Assert.False(ok.Replayed);
        Assert.Equal(2, (await Db.GetQuantitiesAsync(lot)).Failed);
    }

    [Fact(DisplayName = "R6 빈 요청 키는 50400으로 거절된다")]
    public async Task Empty_key_is_rejected()
    {
        var lot = await Db.CreateWaitingLotAsync(10);
        var inspector = await Db.CreateUserAsync("Inspector");
        await AssertSqlErrorAsync(50400, () => Db.ApplyInspectionAsync("", lot, inspector, 1, Pass));
    }
}
