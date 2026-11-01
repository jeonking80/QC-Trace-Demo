using QcTrace.IntegrationTests.Infrastructure;

namespace QcTrace.IntegrationTests.StoredProcedures;

/// <summary>단위 6에서 추가한 SP: LOT 등록, 검사 의뢰, 불합격 처리(재검사·폐기).</summary>
[Collection(SqlServerCollection.Name)]
public class WorkflowTests(SqlServerFixture fixture)
{
    private readonly Db _db = new(fixture);

    [Fact(DisplayName = "LOT 등록은 날짜별 일련번호를 붙이고 등록 상태·미검사 수량·Produce 원장을 만든다")]
    public async Task RegisterLot_CreatesNumberedLot()
    {
        var day = new DateTime(2030, 1, 15);
        var first = await _db.RegisterLot(Db.Key(), "producer", fixture.ItemId, 50, day);
        var second = await _db.RegisterLot(Db.Key(), "producer", fixture.ItemId, 30, day);

        Assert.Equal("LOT-20300115-001", first.Response.GetProperty("lotNo").GetString());
        Assert.Equal("LOT-20300115-002", second.Response.GetProperty("lotNo").GetString());
        var lot = first.Response.GetProperty("lotId").GetInt32();
        Assert.Equal("Registered", await _db.StatusAsync(lot));
        Assert.Equal(new Quantities(50, 0, 0, 0, 0, 0), await _db.QuantitiesAsync(lot));
        await _db.AssertLedgerMatchesAsync(lot);
    }

    [Fact(DisplayName = "LOT 등록: 생산 담당이 아니면 50403, 없는 품목은 50404, 수량 0은 50400")]
    public async Task RegisterLot_Rejections()
    {
        await SqlAssert.ThrowsAsync(50403, () => _db.RegisterLot(Db.Key(), "inspector1", fixture.ItemId, 10, DateTime.Today));
        await SqlAssert.ThrowsAsync(50404, () => _db.RegisterLot(Db.Key(), "producer", 999999, 10, DateTime.Today));
        await SqlAssert.ThrowsAsync(50400, () => _db.RegisterLot(Db.Key(), "producer", fixture.ItemId, 0, DateTime.Today));
    }

    [Fact(DisplayName = "R6 LOT 등록 재전송은 같은 LOT을 돌려주고 새 LOT을 만들지 않는다")]
    public async Task RegisterLot_Replay_ReturnsSameLot()
    {
        var key = Db.Key();
        var day = new DateTime(2030, 2, 1);
        var first = await _db.RegisterLot(key, "producer", fixture.ItemId, 20, day);
        var again = await _db.RegisterLot(key, "producer", fixture.ItemId, 20, day);

        Assert.False(first.Replayed);
        Assert.True(again.Replayed);
        Assert.Equal(first.Response.GetProperty("lotId").GetInt32(), again.Response.GetProperty("lotId").GetInt32());
        Assert.Equal(1, (int)(await _db.ScalarAsync("SELECT COUNT(*) FROM Lots WHERE LotNo LIKE N'LOT-20300201-%'"))!);
        await SqlAssert.ThrowsAsync(50409, () => _db.RegisterLot(key, "producer", fixture.ItemId, 21, day));
    }

    [Fact(DisplayName = "등록 상태 LOT은 검사할 수 없고(52012), 검사 의뢰 뒤에는 검사할 수 있다")]
    public async Task InspectionNeedsRequest()
    {
        var reg = await _db.RegisterLot(Db.Key(), "producer", fixture.ItemId, 10, DateTime.Today);
        var lot = reg.Response.GetProperty("lotId").GetInt32();

        await SqlAssert.ThrowsAsync(52012, () => _db.ApplyInspection(Db.Key(), lot, "inspector1", 5, "Pass"));
        await SqlAssert.ThrowsAsync(50403, () => _db.RequestInspection(Db.Key(), lot, "inspector1"));

        var req = await _db.RequestInspection(Db.Key(), lot, "producer");
        Assert.Equal("Waiting", req.Response.GetProperty("status").GetString());
        Assert.NotNull(await _db.ScalarAsync("SELECT InspectionRequestedAt FROM Lots WHERE Id = @id", ("@id", lot)));

        await SqlAssert.ThrowsAsync(52012, () => _db.RequestInspection(Db.Key(), lot, "producer"));
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 5, "Pass");
        Assert.Equal(new Quantities(5, 5, 0, 0, 0, 0), await _db.QuantitiesAsync(lot));
        await _db.AssertLedgerMatchesAsync(lot);
    }

    [Fact(DisplayName = "불합격 재검사는 미검사로, 폐기는 폐기 수량으로 옮기고 생산 수량 등식이 유지된다")]
    public async Task DisposeFailed_MovesQuantities()
    {
        var lot = await fixture.CreateLotAsync(100);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 30, "Fail", reason: "치수 불량");

        await _db.DisposeFailed(Db.Key(), lot, "inspector2", 10, "Reinspect", "재작업 후 재검사");
        var scrap = await _db.DisposeFailed(Db.Key(), lot, "admin", 15, "Scrap", "수리 불가");

        var q = await _db.QuantitiesAsync(lot);
        Assert.Equal(new Quantities(80, 0, 5, 0, 0, 0, 15), q);
        Assert.Equal(100, q.Uninspected + q.Passed + q.Failed + q.Held + q.Scrapped);
        Assert.Equal(15, scrap.Response.GetProperty("quantities").GetProperty("scrapped").GetInt32());
        await _db.AssertLedgerMatchesAsync(lot);
    }

    [Fact(DisplayName = "불합격 처리: 불합격 수량 초과 52013, 사유 없음 52004, 검사자 아님 50403, 잘못된 방법 50400")]
    public async Task DisposeFailed_Rejections()
    {
        var lot = await fixture.CreateLotAsync(20);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 5, "Fail", reason: "외관 불량");

        await SqlAssert.ThrowsAsync(52013, () => _db.DisposeFailed(Db.Key(), lot, "admin", 6, "Scrap", "폐기"));
        await SqlAssert.ThrowsAsync(52004, () => _db.DisposeFailed(Db.Key(), lot, "admin", 1, "Scrap", null));
        await SqlAssert.ThrowsAsync(52004, () => _db.DisposeFailed(Db.Key(), lot, "admin", 1, "Scrap", "  "));
        await SqlAssert.ThrowsAsync(50403, () => _db.DisposeFailed(Db.Key(), lot, "approver", 1, "Scrap", "폐기"));
        await SqlAssert.ThrowsAsync(50400, () => _db.DisposeFailed(Db.Key(), lot, "inspector1", 1, "Ship", "폐기"));

        Assert.Equal(new Quantities(15, 0, 5, 0, 0, 0), await _db.QuantitiesAsync(lot));
    }

    [Fact(DisplayName = "앱 계정은 Lots에 직접 INSERT할 수 없다(등록은 SP로만)")]
    public async Task AppCannotInsertLotsDirectly()
    {
        var ex = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => _db.ExecuteAsAppAsync(
            $"INSERT INTO Lots (LotNo, ItemId, ProducedQty, ProducedOn, Shift, Status, CreatedById) VALUES (N'X', {fixture.ItemId}, 1, '2030-01-01', N'A', N'Registered', N'user-producer')"));
        Assert.Equal(229, ex.Number);
    }
}
