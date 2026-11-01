using QcTrace.IntegrationTests.Infrastructure;
using QcTrace.Reconcile;

namespace QcTrace.IntegrationTests.Admin;

/// <summary>독립 대조 앱(tools/Reconcile): 앱 코드·DB 뷰 없이 원본 행으로 다시 세어 불일치를 잡는지</summary>
[Collection(SqlServerCollection.Name)]
public class IndependentReconcileTests(SqlServerFixture fixture)
{
    private readonly Db _db = new(fixture);

    private async Task<LotResult> CheckAsync(int lotId)
    {
        var data = await Snapshot.LoadAsync(fixture.AdminConnectionString);
        return Reconciler.Check(data.Lots[lotId], data);
    }

    [Fact(DisplayName = "검사·정정·불합격 처리·승인·출하를 거친 LOT은 독립 대조에서도 일치")]
    public async Task FullFlow_Matches()
    {
        var lot = await fixture.CreateLotAsync(100);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 70, "Pass");
        var fail = await _db.ApplyInspection(Db.Key(), lot, "inspector1", 20, "Fail", reason: "불량");
        await _db.ApplyInspection(Db.Key(), lot, "inspector2", 5, "Pass", reason: "재판정",
            correctsInspectionId: fail.Response.GetProperty("inspectionId").GetInt64());
        await _db.DisposeFailed(Db.Key(), lot, "admin", 10, "Scrap", "폐기");
        await _db.DisposeFailed(Db.Key(), lot, "inspector1", 5, "Reinspect", "재검사");
        await _db.ApproveShipment(Db.Key(), lot, "approver", 50);
        await _db.ShipLot(Db.Key(), lot, "shipper", 30);

        var result = await CheckAsync(lot);
        Assert.Empty(result.Problems);
        Assert.Equal(0, result.Overage);
    }

    [Fact(DisplayName = "R9 초과 검사분은 생산 등식 차이와 같아서 불일치가 아니다")]
    public async Task Overage_IsNotMismatch()
    {
        var lot = await fixture.CreateLotAsync(10);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 8, "Pass");
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 5, "Hold", reason: "추가 확인");

        var result = await CheckAsync(lot);
        Assert.Empty(result.Problems);
        Assert.Equal(3, result.Overage);
    }

    [Theory(DisplayName = "수량·원장·출하 실적을 SP 밖에서 바꾸면 독립 대조가 불일치로 잡는다(근거 행 없는 원장, 검사 행과 안 맞는 원장 포함)")]
    [InlineData("UPDATE LotQuantities SET Passed = Passed + 1, Uninspected = Uninspected - 1 WHERE LotId = @id", "합격 현재")]
    [InlineData("UPDATE LotQuantities SET Reserved = Passed + 1 WHERE LotId = @id", "예약 현재")]
    [InlineData("UPDATE Lots SET ProducedQty = ProducedQty + 5 WHERE Id = @id", "생산 원장")]
    [InlineData("INSERT INTO Shipments (LotId, ShipperId, Qty, Destination) SELECT @id, CreatedById, 1, N'우회' FROM Lots WHERE Id = @id", "출하 실적 합")]
    [InlineData("""
        INSERT INTO Ledger (LotId, Kind, DeltaField, Delta, ActorId, Reason)
        SELECT @id, N'Inspect', f.F, f.D, CreatedById, N'우회' FROM Lots CROSS JOIN (VALUES (N'Failed', -2), (N'Passed', 2)) f(F, D) WHERE Id = @id;
        UPDATE LotQuantities SET Failed = Failed - 2, Passed = Passed + 2 WHERE LotId = @id
        """, "원장 #")]
    [InlineData("""
        INSERT INTO Ledger (LotId, Kind, DeltaField, Delta, ActorId, Reason, SourceTable, SourceId)
        SELECT @id, N'Inspect', f.F, f.D, CreatedById, N'우회', N'Inspections', (SELECT MIN(Id) FROM Inspections WHERE LotId = @id)
        FROM Lots CROSS JOIN (VALUES (N'Failed', -2), (N'Passed', 2)) f(F, D) WHERE Id = @id;
        UPDATE LotQuantities SET Failed = Failed - 2, Passed = Passed + 2 WHERE LotId = @id
        """, "검사 #")]
    public async Task Tampering_IsCaught(string sql, string expected)
    {
        var lot = await fixture.CreateLotAsync(10);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 6, "Pass");
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 3, "Fail", reason: "불량");
        Assert.Empty((await CheckAsync(lot)).Problems);

        await _db.ScalarAsync(sql, ("@id", lot));

        var problems = (await CheckAsync(lot)).Problems;
        Assert.Contains(problems, p => p.StartsWith(expected));
    }
}
