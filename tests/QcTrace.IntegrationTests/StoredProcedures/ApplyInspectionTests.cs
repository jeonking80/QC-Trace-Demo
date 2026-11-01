using QcTrace.IntegrationTests.Infrastructure;

namespace QcTrace.IntegrationTests.StoredProcedures;

[Collection(SqlServerCollection.Name)]
public class ApplyInspectionTests(SqlServerFixture fixture)
{
    private readonly Db _db = new(fixture);

    [Fact(DisplayName = "R2 부분 검사: 100개 중 60개 합격 후 미검사 40, 수량 합 100")]
    public async Task PartialInspection_KeepsSumEqualToProduced()
    {
        var lot = await fixture.CreateLotAsync(100);

        var result = await _db.ApplyInspection(Db.Key(), lot, "inspector1", 60, "Pass");

        Assert.False(result.Replayed);
        var q = await _db.QuantitiesAsync(lot);
        Assert.Equal(new Quantities(40, 60, 0, 0, 0, 0), q);
        Assert.Equal(100, q.Uninspected + q.Passed + q.Failed + q.Held);
        Assert.Equal("Inspecting", await _db.StatusAsync(lot));
        Assert.Equal(60, result.Response.GetProperty("appliedQty").GetInt32());
        await _db.AssertLedgerMatchesAsync(lot);
    }

    [Fact(DisplayName = "R2 나머지 검사까지 끝나면 상태가 Inspected")]
    public async Task FullInspection_SetsInspected()
    {
        var lot = await fixture.CreateLotAsync(100);

        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 60, "Pass");
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 35, "Pass");
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 5, "Fail", reason: "외경 상한 초과");

        Assert.Equal(new Quantities(0, 95, 5, 0, 0, 0), await _db.QuantitiesAsync(lot));
        Assert.Equal("Inspected", await _db.StatusAsync(lot));
        await _db.AssertLedgerMatchesAsync(lot);
    }

    [Theory(DisplayName = "R4 불합격·보류는 사유가 없으면 거절하고 아무것도 기록하지 않는다")]
    [InlineData("Fail", null)]
    [InlineData("Fail", "   ")]
    [InlineData("Hold", null)]
    public async Task FailOrHoldWithoutReason_IsRejected(string verdict, string? reason)
    {
        var lot = await fixture.CreateLotAsync(10);

        await SqlAssert.ThrowsAsync(52004, () => _db.ApplyInspection(Db.Key(), lot, "inspector1", 3, verdict, reason));

        Assert.Equal(new Quantities(10, 0, 0, 0, 0, 0), await _db.QuantitiesAsync(lot));
        Assert.Equal(0, await _db.CountAsync("Inspections", lot));
    }

    [Fact(DisplayName = "R6 같은 요청 키 재전송은 한 번만 반영하고 저장된 응답을 돌려준다")]
    public async Task SameKeyResent_AppliesOnce()
    {
        var lot = await fixture.CreateLotAsync(50);
        var key = Db.Key();

        var first = await _db.ApplyInspection(key, lot, "inspector1", 20, "Pass");
        var second = await _db.ApplyInspection(key, lot, "inspector1", 20, "Pass");

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.Response.GetRawText(), second.Response.GetRawText());
        Assert.Equal(new Quantities(30, 20, 0, 0, 0, 0), await _db.QuantitiesAsync(lot));
        Assert.Equal(1, await _db.CountAsync("Inspections", lot));
    }

    [Fact(DisplayName = "R6 같은 키에 다른 본문이면 409 충돌")]
    public async Task SameKeyDifferentBody_Conflicts()
    {
        var lot = await fixture.CreateLotAsync(50);
        var key = Db.Key();
        await _db.ApplyInspection(key, lot, "inspector1", 20, "Pass");

        await SqlAssert.ThrowsAsync(50409, () => _db.ApplyInspection(key, lot, "inspector1", 21, "Pass"));

        Assert.Equal(new Quantities(30, 20, 0, 0, 0, 0), await _db.QuantitiesAsync(lot));
    }

    [Fact(DisplayName = "R9 미검사보다 많이 검사하면 초과분은 보류로 기록한다")]
    public async Task OverInspection_GoesToHeld()
    {
        var lot = await fixture.CreateLotAsync(10);

        var result = await _db.ApplyInspection(Db.Key(), lot, "inspector1", 12, "Pass");

        Assert.Equal(new Quantities(0, 10, 0, 2, 0, 0), await _db.QuantitiesAsync(lot));
        Assert.Equal(2, result.Response.GetProperty("overageQty").GetInt32());
        Assert.Equal(1, await _db.ScalarAsync(
            "SELECT COUNT(*) FROM Ledger WHERE LotId = @id AND DeltaField = 'Held' AND Delta = 2 AND Reason LIKE N'%(R9)%'", ("@id", lot)));
        await _db.AssertLedgerMatchesAsync(lot);
    }

    [Fact(DisplayName = "R3 정정은 원 검사 행을 그대로 두고 새 행과 원장 Correct 행으로 기록한다")]
    public async Task Correction_KeepsOriginalRow()
    {
        var lot = await fixture.CreateLotAsync(10);
        var original = await _db.ApplyInspection(Db.Key(), lot, "inspector1", 10, "Pass");
        var originalId = original.Response.GetProperty("inspectionId").GetInt64();

        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 3, "Fail", reason: "재측정 결과 두께 미달", correctsInspectionId: originalId);

        Assert.Equal(new Quantities(0, 7, 3, 0, 0, 0), await _db.QuantitiesAsync(lot));
        Assert.Equal(2, await _db.CountAsync("Inspections", lot));
        Assert.Equal("Pass", await _db.ScalarAsync("SELECT Verdict FROM Inspections WHERE Id = @id", ("@id", originalId)));
        Assert.Equal(10, await _db.ScalarAsync("SELECT Qty FROM Inspections WHERE Id = @id", ("@id", originalId)));
        Assert.Equal(2, await _db.ScalarAsync("SELECT COUNT(*) FROM Ledger WHERE LotId = @id AND Kind = 'Correct'", ("@id", lot)));
        await _db.AssertLedgerMatchesAsync(lot);
    }

    [Fact(DisplayName = "R3 정정 수량이 원 검사의 남은 수량을 넘으면 거절")]
    public async Task Correction_OverOriginal_IsRejected()
    {
        var lot = await fixture.CreateLotAsync(10);
        var original = await _db.ApplyInspection(Db.Key(), lot, "inspector1", 4, "Pass");
        var originalId = original.Response.GetProperty("inspectionId").GetInt64();
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 3, "Fail", reason: "정정 1", correctsInspectionId: originalId);

        await SqlAssert.ThrowsAsync(52011, () =>
            _db.ApplyInspection(Db.Key(), lot, "inspector1", 2, "Fail", reason: "정정 2", correctsInspectionId: originalId));

        Assert.Equal(new Quantities(6, 1, 3, 0, 0, 0), await _db.QuantitiesAsync(lot));
    }

    [Fact(DisplayName = "검사자 역할이 없으면 검사 실적을 입력할 수 없다")]
    public async Task NonInspector_IsRejected()
    {
        var lot = await fixture.CreateLotAsync(10);

        await SqlAssert.ThrowsAsync(50403, () => _db.ApplyInspection(Db.Key(), lot, "shipper", 1, "Pass"));
    }

    [Fact(DisplayName = "없는 LOT은 404")]
    public async Task UnknownLot_IsNotFound()
    {
        await SqlAssert.ThrowsAsync(50404, () => _db.ApplyInspection(Db.Key(), -1, "inspector1", 1, "Pass"));
    }

    [Fact(DisplayName = "R10 원장 행마다 작업자·시각·사유가 있다")]
    public async Task LedgerRows_HaveActorTimeReason()
    {
        var lot = await fixture.CreateLotAsync(10);
        await _db.ApplyInspection(Db.Key(), lot, "inspector2", 6, "Pass");
        await _db.ApplyInspection(Db.Key(), lot, "inspector2", 4, "Hold", reason: "측정 장비 점검 중");

        Assert.Equal(0, await _db.ScalarAsync("""
            SELECT COUNT(*) FROM Ledger
            WHERE LotId = @id AND (ActorId IS NULL OR CreatedAt IS NULL OR LEN(LTRIM(RTRIM(Reason))) = 0)
            """, ("@id", lot)));
        Assert.Equal(2, await _db.ScalarAsync(
            "SELECT COUNT(*) FROM Ledger WHERE LotId = @id AND ActorId = @u AND Kind = 'Inspect' AND SourceTable = 'Inspections' AND DeltaField IN ('Uninspected','Held') AND Reason = N'측정 장비 점검 중'",
            ("@id", lot), ("@u", _db.User("inspector2"))));
    }
}
