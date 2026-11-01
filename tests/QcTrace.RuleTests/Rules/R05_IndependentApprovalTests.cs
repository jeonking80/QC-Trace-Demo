using QcTrace.RuleTests.Infrastructure;

namespace QcTrace.RuleTests.Rules;

/// <summary>R5. 검사자 본인은 자기 LOT의 출하를 승인할 수 없다.</summary>
public class R05_IndependentApprovalTests(SqlServerFixture fx) : RuleTestBase(fx)
{
    [Fact(DisplayName = "R5 검사자 본인 승인은 52005로 거절되고 타인 승인은 성공한다")]
    public async Task Self_approval_rejected_other_approval_succeeds()
    {
        var lot = await Db.CreateWaitingLotAsync(10);
        var inspectorApprover = await Db.CreateUserAsync("Inspector", "Approver");
        var otherApprover = await Db.CreateUserAsync("Approver");
        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspectorApprover, 10, Pass);

        await AssertSqlErrorAsync(52005, () => Db.ApproveShipmentAsync(TestDb.NewKey(), lot, inspectorApprover, 5));
        Assert.Equal(0, (await Db.GetQuantitiesAsync(lot)).Reserved);

        var ok = await Db.ApproveShipmentAsync(TestDb.NewKey(), lot, otherApprover, 5);
        Assert.Equal("Approved", ok.Json.GetProperty("decision").GetString());
        Assert.Equal(5, (await Db.GetQuantitiesAsync(lot)).Reserved);
    }

    [Fact(DisplayName = "R5 여러 검사자 중 한 명이라도 검사했으면 그 사람은 승인할 수 없다")]
    public async Task Any_inspector_of_lot_cannot_approve()
    {
        var lot = await Db.CreateWaitingLotAsync(10);
        var inspectorA = await Db.CreateUserAsync("Inspector");
        var inspectorB = await Db.CreateUserAsync("Inspector", "Approver");
        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspectorA, 6, Pass);
        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspectorB, 4, Pass);

        await AssertSqlErrorAsync(52005, () => Db.ApproveShipmentAsync(TestDb.NewKey(), lot, inspectorB, 1));
    }

    [Fact(DisplayName = "R5 정정만 한 검사자도 그 LOT의 출하를 승인할 수 없다")]
    public async Task Corrector_cannot_approve()
    {
        var lot = await Db.CreateWaitingLotAsync(10);
        var inspector = await Db.CreateUserAsync("Inspector");
        var corrector = await Db.CreateUserAsync("Inspector", "Approver");
        var original = await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 10, Fail, reason: "불량");
        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, corrector, 5, Pass, reason: "재검 합격",
            correctsInspectionId: original.Json.GetProperty("inspectionId").GetInt64());

        await AssertSqlErrorAsync(52005, () => Db.ApproveShipmentAsync(TestDb.NewKey(), lot, corrector, 1));
    }

    [Fact(DisplayName = "R5 다른 LOT을 검사한 사람은 이 LOT을 승인할 수 있다")]
    public async Task Inspector_of_other_lot_can_approve()
    {
        var lotA = await Db.CreateWaitingLotAsync(10);
        var (lotB, _) = await CreatePassedLotAsync(10, 10);
        var person = await Db.CreateUserAsync("Inspector", "Approver");
        await Db.ApplyInspectionAsync(TestDb.NewKey(), lotA, person, 10, Pass);

        var ok = await Db.ApproveShipmentAsync(TestDb.NewKey(), lotB, person, 10);
        Assert.Equal(10, ok.Json.GetProperty("approvedQty").GetInt32());
    }

    [Fact(DisplayName = "R5 승인 역할이 없는 사용자의 승인은 50403으로 거절된다")]
    public async Task Approval_requires_approver_role()
    {
        var (lot, _) = await CreatePassedLotAsync(10, 10);
        var notApprover = await Db.CreateUserAsync("Shipper");

        await AssertSqlErrorAsync(50403, () => Db.ApproveShipmentAsync(TestDb.NewKey(), lot, notApprover, 1));
    }
}
