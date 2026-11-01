using QcTrace.IntegrationTests.Infrastructure;

namespace QcTrace.IntegrationTests.Admin;

/// <summary>vw_LotReconciliation: 원장 합·생산 등식·출하 범위를 DB에서 바로 판정</summary>
[Collection(SqlServerCollection.Name)]
public class ReconciliationTests(SqlServerFixture fixture)
{
    private readonly Db _db = new(fixture);

    private async Task<(string State, int Diff, int Overage, bool LedgerMatches)> RowAsync(int lot)
    {
        var state = (string)(await _db.ScalarAsync("SELECT State FROM vw_LotReconciliation WHERE LotId = @id", ("@id", lot)))!;
        var diff = (int)(await _db.ScalarAsync("SELECT ProductionDiff FROM vw_LotReconciliation WHERE LotId = @id", ("@id", lot)))!;
        var over = (int)(await _db.ScalarAsync("SELECT OverageQty FROM vw_LotReconciliation WHERE LotId = @id", ("@id", lot)))!;
        var match = (bool)(await _db.ScalarAsync("SELECT LedgerMatches FROM vw_LotReconciliation WHERE LotId = @id", ("@id", lot)))!;
        return (state, diff, over, match);
    }

    [Fact(DisplayName = "검사·정정·불합격 처리·승인·출하를 거친 LOT은 정상(OK)")]
    public async Task FullFlow_IsOk()
    {
        var lot = await fixture.CreateLotAsync(100);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 70, "Pass");
        var fail = await _db.ApplyInspection(Db.Key(), lot, "inspector1", 20, "Fail", reason: "불량");
        await _db.ApplyInspection(Db.Key(), lot, "inspector2", 5, "Pass", reason: "재판정",
            correctsInspectionId: fail.Response.GetProperty("inspectionId").GetInt64());
        await _db.DisposeFailed(Db.Key(), lot, "admin", 10, "Scrap", "폐기");
        await _db.ApproveShipment(Db.Key(), lot, "approver", 50);
        await _db.ShipLot(Db.Key(), lot, "shipper", 30);

        Assert.Equal(("OK", 0, 0, true), await RowAsync(lot));
    }

    [Fact(DisplayName = "R9 초과 검사분은 불일치가 아니라 초과 보류로 따로 표시된다")]
    public async Task Overage_IsSeparated()
    {
        var lot = await fixture.CreateLotAsync(10);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 8, "Pass");
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 5, "Hold", reason: "추가 확인");  // 2 반영 + 3 초과

        Assert.Equal(("OverageHeld", 3, 3, true), await RowAsync(lot));
    }

    [Fact(DisplayName = "현재 수량이 원장 합과 다르면 불일치로 잡힌다")]
    public async Task TamperedQuantity_IsMismatch()
    {
        var lot = await fixture.CreateLotAsync(10);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 4, "Pass");
        await _db.ScalarAsync("UPDATE LotQuantities SET Passed = Passed + 1, Uninspected = Uninspected - 1 WHERE LotId = @id", ("@id", lot));

        var row = await RowAsync(lot);
        Assert.Equal("Mismatch", row.State);
        Assert.False(row.LedgerMatches);
    }

    [Fact(DisplayName = "원장과 같이 어긋나도 생산 등식 차이가 초과분과 다르면 불일치")]
    public async Task ProductionEquationBroken_IsMismatch()
    {
        var lot = await fixture.CreateLotAsync(10);
        await _db.ScalarAsync("UPDATE Lots SET ProducedQty = 12 WHERE Id = @id", ("@id", lot));

        var row = await RowAsync(lot);
        Assert.Equal("Mismatch", row.State);
        Assert.True(row.LedgerMatches);
        Assert.Equal(-2, row.Diff);
    }
}
