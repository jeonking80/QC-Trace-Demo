using QcTrace.RuleTests.Infrastructure;

namespace QcTrace.RuleTests.Rules;

/// <summary>R2. 부분 검사를 허용하고, 합격·불합격·보류·미검사 수량을 각각 유지한다.</summary>
public class R02_PartialInspectionTests(SqlServerFixture fx) : RuleTestBase(fx)
{
    [Fact(DisplayName = "R2 100개 중 60개 검사 후 네 수량의 합이 100이다")]
    public async Task Partial_inspection_keeps_total()
    {
        var lot = await Db.CreateWaitingLotAsync(100);
        var inspector = await Db.CreateUserAsync("Inspector");

        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 40, Pass);
        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 15, Fail, reason: "외관 불량");
        var last = await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 5, Hold, reason: "판정 보류");

        var q = await Db.GetQuantitiesAsync(lot);
        Assert.Equal(new Quantities(40, 40, 15, 5, 0, 0), q);
        Assert.Equal(100, q.Uninspected + q.Passed + q.Failed + q.Held);

        // SP 응답의 수량도 테이블과 같아야 한다
        Assert.Equal(40, QtyOf(last, "uninspected"));
        Assert.Equal(40, QtyOf(last, "passed"));
        Assert.Equal(15, QtyOf(last, "failed"));
        Assert.Equal(5, QtyOf(last, "held"));

        await AssertLedgerMatchesQuantitiesAsync(lot);
    }

    [Fact(DisplayName = "R2 부분 검사 중에는 Inspecting, 전량 검사 후에는 Inspected 상태다")]
    public async Task Status_follows_partial_and_complete()
    {
        var lot = await Db.CreateWaitingLotAsync(100);
        var inspector = await Db.CreateUserAsync("Inspector");

        var first = await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 60, Pass);
        Assert.Equal("Inspecting", await Db.GetLotStatusAsync(lot));
        Assert.Equal("Inspecting", first.Json.GetProperty("status").GetString());

        var second = await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 40, Pass);
        Assert.Equal("Inspected", await Db.GetLotStatusAsync(lot));
        Assert.Equal("Inspected", second.Json.GetProperty("status").GetString());
        Assert.Equal(0, QtyOf(second, "uninspected"));
    }

    [Fact(DisplayName = "R2 검사를 여러 번 나눠도 검사 행이 각각 남고 수량이 누적된다")]
    public async Task Each_partial_inspection_is_recorded()
    {
        var lot = await Db.CreateWaitingLotAsync(30);
        var inspector = await Db.CreateUserAsync("Inspector");

        for (var i = 0; i < 3; i++)
            await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 10, Pass);

        Assert.Equal(3, await Db.CountAsync("Inspections", lot));
        Assert.Equal(new Quantities(0, 30, 0, 0, 0, 0), await Db.GetQuantitiesAsync(lot));
        await AssertLedgerMatchesQuantitiesAsync(lot);
    }

    [Theory(DisplayName = "R2 검사 수량 0 이하는 50400으로 거절된다")]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Non_positive_qty_is_rejected(int qty)
    {
        var lot = await Db.CreateWaitingLotAsync(10);
        var inspector = await Db.CreateUserAsync("Inspector");

        await AssertSqlErrorAsync(50400, () => Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, qty, Pass));
        Assert.Equal(new Quantities(10, 0, 0, 0, 0, 0), await Db.GetQuantitiesAsync(lot));
    }

    [Fact(DisplayName = "R2 알 수 없는 판정 값은 50400으로 거절된다")]
    public async Task Unknown_verdict_is_rejected()
    {
        var lot = await Db.CreateWaitingLotAsync(10);
        var inspector = await Db.CreateUserAsync("Inspector");

        await AssertSqlErrorAsync(50400, () => Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 1, "Maybe", reason: "?"));
    }
}
