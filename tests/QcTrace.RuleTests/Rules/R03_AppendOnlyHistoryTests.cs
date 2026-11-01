using QcTrace.RuleTests.Infrastructure;

namespace QcTrace.RuleTests.Rules;

/// <summary>R3. 판정과 수량 변경은 덮어쓰지 않고 이력에 추가한다.</summary>
public class R03_AppendOnlyHistoryTests(SqlServerFixture fx) : RuleTestBase(fx)
{
    private static long InspectionId(SpResult r) => r.Json.GetProperty("inspectionId").GetInt64();

    [Fact(DisplayName = "R3 정정 후 원 검사 행이 그대로 남고 정정 행이 원 행을 가리킨다")]
    public async Task Correction_keeps_original_row()
    {
        var lot = await Db.CreateWaitingLotAsync(100);
        var inspector = await Db.CreateUserAsync("Inspector");

        var original = await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 10, Fail, reason: "치수 불량");
        var originalId = InspectionId(original);

        var correction = await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 4, Pass,
            reason: "재측정 결과 합격", correctsInspectionId: originalId);
        var correctionId = InspectionId(correction);
        Assert.NotEqual(originalId, correctionId);

        // 원 행: 수량·판정·사유 그대로
        Assert.Equal(10, await Db.ScalarAdminAsync<int>("SELECT Qty FROM Inspections WHERE Id = @id", ("@id", originalId)));
        Assert.Equal("Fail", await Db.ScalarAdminAsync<string>("SELECT Verdict FROM Inspections WHERE Id = @id", ("@id", originalId)));
        Assert.Equal("치수 불량", await Db.ScalarAdminAsync<string>("SELECT Reason FROM Inspections WHERE Id = @id", ("@id", originalId)));
        Assert.Equal(0, await Db.ScalarAdminAsync<int>(
            "SELECT COUNT(*) FROM Inspections WHERE Id = @id AND CorrectsInspectionId IS NOT NULL", ("@id", originalId)));

        // 정정 행: 원 행 참조
        Assert.Equal(originalId, await Db.ScalarAdminAsync<long>(
            "SELECT CorrectsInspectionId FROM Inspections WHERE Id = @id", ("@id", correctionId)));
        Assert.Equal(2, await Db.CountAsync("Inspections", lot));

        // 수량: 불합격 10 중 4가 합격으로 옮겨감
        var q = await Db.GetQuantitiesAsync(lot);
        Assert.Equal(new Quantities(90, 4, 6, 0, 0, 0), q);
        await AssertLedgerMatchesQuantitiesAsync(lot);
    }

    [Fact(DisplayName = "R3 정정은 원장에 새 행으로 남고 이전 원장 행은 바뀌지 않는다")]
    public async Task Correction_appends_ledger_rows()
    {
        var lot = await Db.CreateWaitingLotAsync(50);
        var inspector = await Db.CreateUserAsync("Inspector");

        var original = await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 20, Hold, reason: "외관 확인 필요");
        var before = await Db.GetLedgerAsync(lot);

        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 20, Pass,
            reason: "외관 확인 완료", correctsInspectionId: InspectionId(original));
        var after = await Db.GetLedgerAsync(lot);

        Assert.True(after.Count > before.Count, "정정 후 원장 행이 늘어야 한다");
        Assert.Equal(before, after.Take(before.Count).ToList());
        Assert.Contains(after.Skip(before.Count), r => r.Kind == "Correct");
        await AssertLedgerMatchesQuantitiesAsync(lot);
    }

    [Fact(DisplayName = "R3 원 검사 수량보다 많이 정정하면 52011로 거절된다")]
    public async Task Correction_over_original_qty_is_rejected()
    {
        var lot = await Db.CreateWaitingLotAsync(100);
        var inspector = await Db.CreateUserAsync("Inspector");
        var original = await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 10, Fail, reason: "불량");

        await AssertSqlErrorAsync(52011, () => Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 11, Pass,
            reason: "정정", correctsInspectionId: InspectionId(original)));

        Assert.Equal(new Quantities(90, 0, 10, 0, 0, 0), await Db.GetQuantitiesAsync(lot));
    }

    [Fact(DisplayName = "R3 이미 정정한 만큼을 빼고 남은 수량까지만 다시 정정할 수 있다")]
    public async Task Repeated_correction_limited_to_remaining()
    {
        var lot = await Db.CreateWaitingLotAsync(100);
        var inspector = await Db.CreateUserAsync("Inspector");
        var original = await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 10, Fail, reason: "불량");
        var id = InspectionId(original);

        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 4, Pass, reason: "정정1", correctsInspectionId: id);
        await AssertSqlErrorAsync(52011, () => Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 7, Pass,
            reason: "정정2", correctsInspectionId: id));
        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 6, Pass, reason: "정정3", correctsInspectionId: id);

        Assert.Equal(new Quantities(90, 10, 0, 0, 0, 0), await Db.GetQuantitiesAsync(lot));
        await AssertLedgerMatchesQuantitiesAsync(lot);
    }

    [Fact(DisplayName = "R3 이미 출하 승인된 합격분은 정정으로 빼낼 수 없다(52011)")]
    public async Task Correction_cannot_take_reserved_passed_qty()
    {
        var lot = await Db.CreateWaitingLotAsync(10);
        var inspector = await Db.CreateUserAsync("Inspector");
        var approver = await Db.CreateUserAsync("Approver");
        var original = await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 10, Pass);
        await Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 10);

        await AssertSqlErrorAsync(52011, () => Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 1, Fail,
            reason: "승인 후 불량 발견", correctsInspectionId: InspectionId(original)));

        var q = await Db.GetQuantitiesAsync(lot);
        Assert.True(q.Available >= 0);
        Assert.Equal(10, q.Reserved);
    }

    [Fact(DisplayName = "R3 없는 원 검사를 정정하면 50404로 거절된다")]
    public async Task Correction_of_missing_inspection_is_rejected()
    {
        var lot = await Db.CreateWaitingLotAsync(10);
        var inspector = await Db.CreateUserAsync("Inspector");

        await AssertSqlErrorAsync(50404, () => Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 1, Pass,
            reason: "정정", correctsInspectionId: long.MaxValue));
    }

    [Fact(DisplayName = "R3 다른 LOT의 검사 행을 정정 대상으로 쓰면 거절된다")]
    public async Task Correction_of_other_lots_inspection_is_rejected()
    {
        var lotA = await Db.CreateWaitingLotAsync(10);
        var lotB = await Db.CreateWaitingLotAsync(10);
        var inspector = await Db.CreateUserAsync("Inspector");
        var onA = await Db.ApplyInspectionAsync(TestDb.NewKey(), lotA, inspector, 5, Fail, reason: "불량");

        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => Db.ApplyInspectionAsync(TestDb.NewKey(),
            lotB, inspector, 1, Pass, reason: "정정", correctsInspectionId: InspectionId(onA)));

        Assert.Equal(new Quantities(10, 0, 0, 0, 0, 0), await Db.GetQuantitiesAsync(lotB));
        Assert.Equal(new Quantities(5, 0, 5, 0, 0, 0), await Db.GetQuantitiesAsync(lotA));
    }

    [Theory(DisplayName = "R3 이력 테이블 UPDATE는 관리자 계정이어도 트리거가 51000으로 거부한다")]
    [InlineData("Inspections", "Qty = Qty + 1")]
    [InlineData("ShipmentApprovals", "ApprovedQty = ApprovedQty + 1")]
    [InlineData("Shipments", "Qty = Qty + 1")]
    [InlineData("Ledger", "Delta = Delta + 1")]
    public async Task Update_on_history_is_rejected_by_trigger(string table, string set)
    {
        var lot = await CreateFullFlowLotAsync();
        await AssertSqlErrorAsync(51000, () => Db.ExecAdminAsync($"UPDATE {table} SET {set} WHERE LotId = @id", ("@id", lot)));
    }

    [Theory(DisplayName = "R3 이력 테이블 DELETE는 관리자 계정이어도 트리거가 51000으로 거부한다")]
    [InlineData("Inspections")]
    [InlineData("ShipmentApprovals")]
    [InlineData("Shipments")]
    [InlineData("Ledger")]
    public async Task Delete_on_history_is_rejected_by_trigger(string table)
    {
        var lot = await CreateFullFlowLotAsync();
        var before = await Db.CountAsync(table, lot);
        await AssertSqlErrorAsync(51000, () => Db.ExecAdminAsync($"DELETE FROM {table} WHERE LotId = @id", ("@id", lot)));
        Assert.Equal(before, await Db.CountAsync(table, lot));
    }

    [Theory(DisplayName = "R3 앱 계정은 이력·수량 테이블을 직접 고칠 수 없다(권한 오류 229)")]
    [InlineData("UPDATE Inspections SET Qty = 0 WHERE LotId = @id")]
    [InlineData("DELETE FROM Ledger WHERE LotId = @id")]
    [InlineData("UPDATE LotQuantities SET Passed = 9999 WHERE LotId = @id")]
    [InlineData("INSERT INTO Ledger (LotId, Kind, DeltaField, Delta, ActorId, Reason) VALUES (@id, 'Inspect', 'Passed', 1, 'x', 'x')")]
    public async Task App_account_cannot_write_history_directly(string sql)
    {
        var lot = await CreateFullFlowLotAsync();
        var before = await Db.GetQuantitiesAsync(lot);
        await AssertSqlErrorAsync(229, () => Db.ExecAppAsync(sql, ("@id", lot)));
        Assert.Equal(before, await Db.GetQuantitiesAsync(lot));
    }

    private async Task<int> CreateFullFlowLotAsync()
    {
        var lot = await Db.CreateWaitingLotAsync(10);
        var inspector = await Db.CreateUserAsync("Inspector");
        var approver = await Db.CreateUserAsync("Approver");
        var shipper = await Db.CreateUserAsync("Shipper");
        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 10, Pass);
        await Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 5);
        await Db.ShipLotAsync(TestDb.NewKey(), lot, shipper, 5);
        return lot;
    }
}
