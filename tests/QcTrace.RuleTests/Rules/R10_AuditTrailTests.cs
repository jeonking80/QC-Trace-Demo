using QcTrace.RuleTests.Infrastructure;

namespace QcTrace.RuleTests.Rules;

/// <summary>R10. 모든 기록에 작업자·시각·사유를 남긴다.</summary>
public class R10_AuditTrailTests(SqlServerFixture fx) : RuleTestBase(fx)
{
    [Fact(DisplayName = "R10 검사·불합격·정정·승인·출하 후 원장 모든 행에 작업자·시각·사유가 있다")]
    public async Task Every_ledger_row_has_actor_time_reason()
    {
        var start = await Db.ScalarAdminAsync<DateTime>("SELECT SYSUTCDATETIME()");
        var lot = await Db.CreateWaitingLotAsync(100);
        var inspector = await Db.CreateUserAsync("Inspector");
        var approver = await Db.CreateUserAsync("Approver");
        var shipper = await Db.CreateUserAsync("Shipper");

        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 60, Pass);
        var fail = await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 20, Fail, reason: "치수 초과");
        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 5, Pass, reason: "재측정 합격",
            correctsInspectionId: fail.Json.GetProperty("inspectionId").GetInt64());
        await Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 50);
        await Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 5, "Rejected", reason: "고객 일정 변경");
        await Db.ShipLotAsync(TestDb.NewKey(), lot, shipper, 30);
        var end = await Db.ScalarAdminAsync<DateTime>("SELECT SYSUTCDATETIME()");

        var ledger = await Db.GetLedgerAsync(lot);
        Assert.NotEmpty(ledger);
        Assert.All(ledger, row =>
        {
            Assert.False(string.IsNullOrWhiteSpace(row.ActorId), $"원장 {row.Id} 작업자 없음");
            Assert.False(string.IsNullOrWhiteSpace(row.Reason), $"원장 {row.Id}({row.Kind}/{row.DeltaField}) 사유 없음");
            Assert.InRange(row.CreatedAt, start.AddSeconds(-5), end.AddSeconds(5));
        });

        // 작업 종류별 작업자가 실제 요청자와 같다
        Assert.All(ledger.Where(r => r.Kind is "Inspect" or "Correct"), r => Assert.Equal(inspector, r.ActorId));
        Assert.All(ledger.Where(r => r.Kind == "Approve"), r => Assert.Equal(approver, r.ActorId));
        Assert.All(ledger.Where(r => r.Kind == "Ship"), r => Assert.Equal(shipper, r.ActorId));
        Assert.Contains(ledger, r => r.Kind == "Inspect");
        Assert.Contains(ledger, r => r.Kind == "Correct");
        Assert.Contains(ledger, r => r.Kind == "Approve");
        Assert.Contains(ledger, r => r.Kind == "Ship");

        // 사용자가 준 사유는 그대로 원장에 남는다
        Assert.Contains(ledger, r => r.Reason.Contains("치수 초과"));
        Assert.Contains(ledger, r => r.Reason.Contains("재측정 합격"));
    }

    [Fact(DisplayName = "R10 원장 행은 출처 테이블과 행을 가리키고, 그 행이 실제로 있다")]
    public async Task Ledger_rows_point_to_source()
    {
        var lot = await Db.CreateWaitingLotAsync(10);
        var inspector = await Db.CreateUserAsync("Inspector");
        var approver = await Db.CreateUserAsync("Approver");
        var shipper = await Db.CreateUserAsync("Shipper");
        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 10, Pass);
        await Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 10);
        await Db.ShipLotAsync(TestDb.NewKey(), lot, shipper, 10);

        var ledger = (await Db.GetLedgerAsync(lot)).Where(r => r.Kind != "Produce").ToList();
        Assert.NotEmpty(ledger);
        foreach (var row in ledger)
        {
            Assert.False(string.IsNullOrEmpty(row.SourceTable), $"원장 {row.Id} SourceTable 없음");
            Assert.NotNull(row.SourceId);
            Assert.Contains(row.SourceTable, new[] { "Inspections", "ShipmentApprovals", "Shipments" });
            var exists = await Db.ScalarAdminAsync<int>(
                $"SELECT COUNT(*) FROM {row.SourceTable} WHERE Id = @id AND LotId = @lot", ("@id", row.SourceId), ("@lot", lot));
            Assert.Equal(1, exists);
        }
    }

    [Fact(DisplayName = "R10 검사·승인·출하 행에 작업자와 시각이 기록된다")]
    public async Task Record_rows_have_actor_and_time()
    {
        var lot = await Db.CreateWaitingLotAsync(10);
        var inspector = await Db.CreateUserAsync("Inspector");
        var approver = await Db.CreateUserAsync("Approver");
        var shipper = await Db.CreateUserAsync("Shipper");
        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 10, Pass);
        await Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 10);
        await Db.ShipLotAsync(TestDb.NewKey(), lot, shipper, 10);

        Assert.Equal(1, await Db.ScalarAdminAsync<int>(
            "SELECT COUNT(*) FROM Inspections WHERE LotId = @l AND InspectorId = @u AND CreatedAt IS NOT NULL", ("@l", lot), ("@u", inspector)));
        Assert.Equal(1, await Db.ScalarAdminAsync<int>(
            "SELECT COUNT(*) FROM ShipmentApprovals WHERE LotId = @l AND ApproverId = @u AND CreatedAt IS NOT NULL", ("@l", lot), ("@u", approver)));
        Assert.Equal(1, await Db.ScalarAdminAsync<int>(
            "SELECT COUNT(*) FROM Shipments WHERE LotId = @l AND ShipperId = @u AND CreatedAt IS NOT NULL", ("@l", lot), ("@u", shipper)));
    }

    [Fact(DisplayName = "R10 검사 역할이 없는 사용자의 검사는 50403으로 거절된다")]
    public async Task Inspection_requires_inspector_role()
    {
        var lot = await Db.CreateWaitingLotAsync(10);
        var producer = await Db.CreateUserAsync("Producer");
        await AssertSqlErrorAsync(50403, () => Db.ApplyInspectionAsync(TestDb.NewKey(), lot, producer, 1, Pass));
    }

    [Fact(DisplayName = "R10 존재하지 않는 작업자 Id는 50403으로 거절된다")]
    public async Task Unknown_actor_is_rejected()
    {
        var lot = await Db.CreateWaitingLotAsync(10);
        await AssertSqlErrorAsync(50403, () => Db.ApplyInspectionAsync(TestDb.NewKey(), lot, Guid.NewGuid().ToString(), 1, Pass));
    }

    [Fact(DisplayName = "R10 없는 LOT에 대한 요청은 50404로 거절된다")]
    public async Task Unknown_lot_is_rejected()
    {
        var inspector = await Db.CreateUserAsync("Inspector");
        await AssertSqlErrorAsync(50404, () => Db.ApplyInspectionAsync(TestDb.NewKey(), int.MaxValue, inspector, 1, Pass));
    }
}
