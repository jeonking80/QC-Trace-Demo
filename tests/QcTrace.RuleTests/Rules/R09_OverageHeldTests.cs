using QcTrace.RuleTests.Infrastructure;

namespace QcTrace.RuleTests.Rules;

/// <summary>R9. 생산 수량보다 많이 검사 등록된 경우 초과분은 보류로 기록하고 삭제하지 않는다.</summary>
public class R09_OverageHeldTests(SqlServerFixture fx) : RuleTestBase(fx)
{
    [Fact(DisplayName = "R9 생산 100에 합격 120 입력 시 100은 합격, 초과 20은 보류로 기록된다")]
    public async Task Overage_goes_to_held()
    {
        var lot = await Db.CreateWaitingLotAsync(100);
        var inspector = await Db.CreateUserAsync("Inspector");

        var r = await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 120, Pass, reason: "현장 수량 과입력");

        Assert.Equal(100, r.Json.GetProperty("appliedQty").GetInt32());
        Assert.Equal(20, r.Json.GetProperty("overageQty").GetInt32());
        // 계약서: 초과분은 Held에 더해지고 Uninspected는 줄지 않는다
        Assert.Equal(new Quantities(0, 100, 0, 20, 0, 0), await Db.GetQuantitiesAsync(lot));
        await AssertLedgerMatchesQuantitiesAsync(lot);
    }

    [Fact(DisplayName = "R9 초과 입력도 검사 행이 남고 수량 120이 지워지지 않는다")]
    public async Task Overage_inspection_row_is_kept()
    {
        var lot = await Db.CreateWaitingLotAsync(100);
        var inspector = await Db.CreateUserAsync("Inspector");

        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 120, Pass, reason: "과입력");

        Assert.Equal(120, await Db.ScalarAdminAsync<int>("SELECT ISNULL(SUM(Qty), 0) FROM Inspections WHERE LotId = @id", ("@id", lot)));
        Assert.Contains(await Db.GetLedgerAsync(lot), l => l.DeltaField == "Held" && l.Delta == 20);
    }

    [Fact(DisplayName = "R9 전량 검사 뒤 추가 입력은 전부 보류로 들어간다")]
    public async Task Inspection_after_complete_goes_to_held()
    {
        var lot = await Db.CreateWaitingLotAsync(50);
        var inspector = await Db.CreateUserAsync("Inspector");
        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 50, Pass);

        var r = await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 10, Pass, reason: "추가 입력");

        Assert.Equal(0, r.Json.GetProperty("appliedQty").GetInt32());
        Assert.Equal(10, r.Json.GetProperty("overageQty").GetInt32());
        Assert.Equal(new Quantities(0, 50, 0, 10, 0, 0), await Db.GetQuantitiesAsync(lot));
    }

    [Fact(DisplayName = "R9 초과 보류분은 출하 가능 수량에 들어가지 않는다")]
    public async Task Overage_is_not_shippable()
    {
        var lot = await Db.CreateWaitingLotAsync(100);
        var inspector = await Db.CreateUserAsync("Inspector");
        var approver = await Db.CreateUserAsync("Approver");
        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 120, Pass, reason: "과입력");

        Assert.Equal(100, (await Db.GetQuantitiesAsync(lot)).Available);
        await AssertSqlErrorAsync(52001, () => Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 101));
    }

    [Fact(DisplayName = "R9 불합격 판정의 초과분도 보류로 기록된다")]
    public async Task Fail_overage_goes_to_held()
    {
        var lot = await Db.CreateWaitingLotAsync(10);
        var inspector = await Db.CreateUserAsync("Inspector");

        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 15, Fail, reason: "전량 불량");

        Assert.Equal(new Quantities(0, 0, 10, 5, 0, 0), await Db.GetQuantitiesAsync(lot));
        await AssertLedgerMatchesQuantitiesAsync(lot);
    }
}
