using Microsoft.Data.SqlClient;
using QcTrace.IntegrationTests.Infrastructure;

namespace QcTrace.IntegrationTests.StoredProcedures;

[Collection(SqlServerCollection.Name)]
public class ConcurrencyAndPermissionTests(SqlServerFixture fixture)
{
    private readonly Db _db = new(fixture);

    [Fact(DisplayName = "R8 같은 LOT에 동시 20요청(각 5개)을 보내도 최종 수량이 맞는다")]
    public async Task TwentyConcurrentInspections_AreSerialized()
    {
        var lot = await fixture.CreateLotAsync(100);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            _db.ApplyInspection(Db.Key(), lot, i % 2 == 0 ? "inspector1" : "inspector2", 5, "Pass")));

        Assert.Equal(new Quantities(0, 100, 0, 0, 0, 0), await _db.QuantitiesAsync(lot));
        Assert.Equal(20, await _db.CountAsync("Inspections", lot));
        Assert.Equal("Inspected", await _db.StatusAsync(lot));
        await _db.AssertLedgerMatchesAsync(lot);
    }

    [Fact(DisplayName = "R8 동시 승인 요청이 합격 수량을 넘지 않는다")]
    public async Task ConcurrentApprovals_DoNotOverReserve()
    {
        var lot = await fixture.CreateLotAsync(50);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 50, "Pass");

        // 10개씩 10건 = 100개 요청, 합격은 50개 → 5건만 성공해야 한다
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            try { await _db.ApproveShipment(Db.Key(), lot, "approver", 10); return true; }
            catch (SqlException ex) when (ex.Number == 52001) { return false; }
        }));

        Assert.Equal(5, results.Count(ok => ok));
        Assert.Equal(50, (await _db.QuantitiesAsync(lot)).Reserved);
        await _db.AssertLedgerMatchesAsync(lot);
    }

    [Fact(DisplayName = "R6 같은 키로 동시 5요청을 보내도 한 번만 반영된다")]
    public async Task ConcurrentSameKey_AppliesOnce()
    {
        var lot = await fixture.CreateLotAsync(100);
        var key = Db.Key();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            _db.ApplyInspection(key, lot, "inspector1", 10, "Pass")));

        Assert.Equal(1, results.Count(r => !r.Replayed));
        Assert.Equal(new Quantities(90, 10, 0, 0, 0, 0), await _db.QuantitiesAsync(lot));
    }

    [Theory(DisplayName = "앱 계정은 수량·이력 테이블을 직접 바꿀 수 없다 (SP만 허용)")]
    [InlineData("UPDATE LotQuantities SET Passed = Passed + 1 WHERE LotId = {0}", 229)]
    [InlineData("UPDATE Lots SET Status = 'Shipped' WHERE Id = {0}", 229)]
    [InlineData("INSERT INTO Ledger (LotId, Kind, DeltaField, Delta, ActorId, Reason) SELECT {0}, 'Inspect', 'Passed', 1, CreatedById, N'우회' FROM Lots WHERE Id = {0}", 229)]
    [InlineData("DELETE FROM Inspections WHERE LotId = {0}", 229)]
    [InlineData("DELETE FROM QcRequests", 229)]
    [InlineData("TRUNCATE TABLE Ledger", 1088)]
    public async Task AppLogin_CannotWriteQuantityTablesDirectly(string sqlTemplate, int expectedError)
    {
        var lot = await fixture.CreateLotAsync(10);
        var before = await _db.QuantitiesAsync(lot);

        var ex = await Assert.ThrowsAsync<SqlException>(() => _db.ExecuteAsAppAsync(string.Format(sqlTemplate, lot)));

        Assert.Equal(expectedError, ex.Number);
        Assert.Equal(before, await _db.QuantitiesAsync(lot));
        await _db.AssertLedgerMatchesAsync(lot);
    }
}
