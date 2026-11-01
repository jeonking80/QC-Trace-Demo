using QcTrace.RuleTests.Infrastructure;

namespace QcTrace.RuleTests.Rules;

/// <summary>R4. 불합격 처리는 사유가 없으면 저장하지 않는다. (보류·정정·반려도 같은 오류 번호, 계약서)</summary>
public class R04_ReasonRequiredTests(SqlServerFixture fx) : RuleTestBase(fx)
{
    [Theory(DisplayName = "R4 사유 없는 불합격·보류는 52004로 거절되고 아무것도 저장되지 않는다")]
    [InlineData("Fail", null)]
    [InlineData("Fail", "")]
    [InlineData("Fail", "   ")]
    [InlineData("Hold", null)]
    [InlineData("Hold", "")]
    [InlineData("Hold", "   ")]
    public async Task Fail_or_hold_without_reason_is_rejected(string verdict, string? reason)
    {
        var lot = await Db.CreateWaitingLotAsync(10);
        var inspector = await Db.CreateUserAsync("Inspector");
        var key = TestDb.NewKey();
        var ledgerBefore = (await Db.GetLedgerAsync(lot)).Count;

        await AssertSqlErrorAsync(52004, () => Db.ApplyInspectionAsync(key, lot, inspector, 3, verdict, reason: reason));

        Assert.Equal(new Quantities(10, 0, 0, 0, 0, 0), await Db.GetQuantitiesAsync(lot));
        Assert.Equal(0, await Db.CountAsync("Inspections", lot));
        Assert.Equal(ledgerBefore, (await Db.GetLedgerAsync(lot)).Count);
        Assert.Equal(0, await Db.CountRequestsAsync(key));
    }

    [Fact(DisplayName = "R4 사유가 있는 불합격은 저장되고 사유가 검사 행과 원장에 남는다")]
    public async Task Fail_with_reason_is_saved()
    {
        var lot = await Db.CreateWaitingLotAsync(10);
        var inspector = await Db.CreateUserAsync("Inspector");

        var r = await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 3, Fail, reason: "버 발생");
        var id = r.Json.GetProperty("inspectionId").GetInt64();

        Assert.Equal("버 발생", await Db.ScalarAdminAsync<string>("SELECT Reason FROM Inspections WHERE Id = @id", ("@id", id)));
        Assert.Contains(await Db.GetLedgerAsync(lot), l => l.DeltaField == "Failed" && l.Reason.Contains("버 발생"));
        Assert.Equal(new Quantities(7, 0, 3, 0, 0, 0), await Db.GetQuantitiesAsync(lot));
    }

    [Fact(DisplayName = "R4 사유 없는 정정은 52004로 거절된다")]
    public async Task Correction_without_reason_is_rejected()
    {
        var lot = await Db.CreateWaitingLotAsync(10);
        var inspector = await Db.CreateUserAsync("Inspector");
        var original = await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 5, Pass);

        await AssertSqlErrorAsync(52004, () => Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 2, Fail,
            reason: null, correctsInspectionId: original.Json.GetProperty("inspectionId").GetInt64()));
        Assert.Equal(new Quantities(5, 5, 0, 0, 0, 0), await Db.GetQuantitiesAsync(lot));
    }

    [Fact(DisplayName = "R4 사유 없는 출하 반려는 52004로 거절된다")]
    public async Task Rejection_without_reason_is_rejected()
    {
        var (lot, _) = await CreatePassedLotAsync(10, 10);
        var approver = await Db.CreateUserAsync("Approver");

        await AssertSqlErrorAsync(52004, () => Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 5, "Rejected", reason: null));
        Assert.Equal(0, await Db.CountAsync("ShipmentApprovals", lot));
    }

    [Fact(DisplayName = "R4 사유 있는 반려는 저장되고 예약 수량은 늘지 않는다")]
    public async Task Rejection_with_reason_does_not_reserve()
    {
        var (lot, _) = await CreatePassedLotAsync(10, 10);
        var approver = await Db.CreateUserAsync("Approver");

        await Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 5, "Rejected", reason: "고객 요청 보류");

        Assert.Equal(1, await Db.CountAsync("ShipmentApprovals", lot));
        var q = await Db.GetQuantitiesAsync(lot);
        Assert.Equal(0, q.Reserved);
        Assert.Equal(10, q.Available);
        await AssertLedgerMatchesQuantitiesAsync(lot);
    }
}
