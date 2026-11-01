using Microsoft.Data.SqlClient;
using QcTrace.RuleTests.Infrastructure;

namespace QcTrace.RuleTests.Rules;

/// <summary>R8. 동시 요청은 LOT 단위로 직렬화한다. (PLAN 7장: 동시 20요청 후 수량 일치)</summary>
public class R08_ConcurrencyTests(SqlServerFixture fx) : RuleTestBase(fx)
{
    private const int N = 20;

    /// <summary>요청 N개를 최대한 동시에 출발시키고 각 결과(성공 응답 또는 SqlException)를 모은다.</summary>
    private static async Task<List<(SpResult? Ok, SqlException? Error)>> RunConcurrentAsync(Func<int, Task<SpResult>> call)
    {
        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, N).Select(i => Task.Run(async () =>
        {
            gate.Wait();
            try { return ((SpResult?)await call(i), (SqlException?)null); }
            catch (SqlException ex) { return ((SpResult?)null, ex); }
        })).ToList();
        await Task.Delay(100);
        gate.Set();
        return (await Task.WhenAll(tasks)).ToList();
    }

    [Fact(DisplayName = "R8 동시 검사 20건(각 5개) 후 합격 100, 미검사 0, 원장 합 일치")]
    public async Task Concurrent_inspections_all_applied()
    {
        var lot = await Db.CreateWaitingLotAsync(100);
        var inspector = await Db.CreateUserAsync("Inspector");

        var results = await RunConcurrentAsync(_ => Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 5, Pass));

        Assert.All(results, r => Assert.True(r.Error is null, r.Error?.Message));
        Assert.Equal(new Quantities(0, 100, 0, 0, 0, 0), await Db.GetQuantitiesAsync(lot));
        Assert.Equal(N, await Db.CountAsync("Inspections", lot));
        await AssertLedgerMatchesQuantitiesAsync(lot);
    }

    [Fact(DisplayName = "R8 동시 승인 20건(각 10개, 출하 가능 100) 중 정확히 10건만 성공하고 나머지는 52001")]
    public async Task Concurrent_approvals_never_overbook()
    {
        var (lot, _) = await CreatePassedLotAsync(100, 100);
        var approver = await Db.CreateUserAsync("Approver");

        var results = await RunConcurrentAsync(_ => Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 10));

        Assert.Equal(10, results.Count(r => r.Ok is not null));
        Assert.All(results.Where(r => r.Error is not null), r => Assert.Equal(52001, r.Error!.Number));
        var q = await Db.GetQuantitiesAsync(lot);
        Assert.Equal(100, q.Reserved);
        Assert.Equal(0, q.Available);
        await AssertLedgerMatchesQuantitiesAsync(lot);
    }

    [Fact(DisplayName = "R8 동시 출하 20건(각 5개, 승인 100) 후 출하 100, 예약 0, 음수 없음")]
    public async Task Concurrent_shipments_never_exceed_reserved()
    {
        var (lot, _) = await CreatePassedLotAsync(200, 200);
        var approver = await Db.CreateUserAsync("Approver");
        var shipper = await Db.CreateUserAsync("Shipper");
        await Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 100);

        var results = await RunConcurrentAsync(_ => Db.ShipLotAsync(TestDb.NewKey(), lot, shipper, 5));

        Assert.All(results, r => Assert.True(r.Error is null, r.Error?.Message));
        var q = await Db.GetQuantitiesAsync(lot);
        Assert.Equal(100, q.Shipped);
        Assert.Equal(0, q.Reserved);
        await AssertLedgerMatchesQuantitiesAsync(lot);
    }

    [Fact(DisplayName = "R8 동시 출하 20건(각 10개, 승인 50) 중 정확히 5건만 성공하고 나머지는 52007")]
    public async Task Concurrent_shipments_over_reserved_rejected()
    {
        var (lot, _) = await CreatePassedLotAsync(100, 100);
        var approver = await Db.CreateUserAsync("Approver");
        var shipper = await Db.CreateUserAsync("Shipper");
        await Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 50);

        var results = await RunConcurrentAsync(_ => Db.ShipLotAsync(TestDb.NewKey(), lot, shipper, 10));

        Assert.Equal(5, results.Count(r => r.Ok is not null));
        Assert.All(results.Where(r => r.Error is not null), r => Assert.Equal(52007, r.Error!.Number));
        var q = await Db.GetQuantitiesAsync(lot);
        Assert.Equal(50, q.Shipped);
        Assert.Equal(0, q.Reserved);
    }

    [Fact(DisplayName = "R8 같은 키 동시 20건은 한 번만 반영되고 나머지는 재전송 응답이다")]
    public async Task Concurrent_same_key_applied_once()
    {
        var lot = await Db.CreateWaitingLotAsync(100);
        var inspector = await Db.CreateUserAsync("Inspector");
        var key = TestDb.NewKey();

        var results = await RunConcurrentAsync(_ => Db.ApplyInspectionAsync(key, lot, inspector, 7, Pass));

        Assert.All(results, r => Assert.True(r.Error is null, r.Error?.Message));
        Assert.Equal(1, results.Count(r => r.Ok is { Replayed: false }));
        Assert.Equal(N - 1, results.Count(r => r.Ok is { Replayed: true }));
        Assert.Equal(new Quantities(93, 7, 0, 0, 0, 0), await Db.GetQuantitiesAsync(lot));
        Assert.Equal(1, await Db.CountAsync("Inspections", lot));
    }

    [Fact(DisplayName = "R8 검사·승인·출하가 섞인 동시 요청 후에도 수량 등식과 원장 합이 맞다")]
    public async Task Mixed_concurrent_requests_keep_invariants()
    {
        var lot = await Db.CreateWaitingLotAsync(200);
        var inspector = await Db.CreateUserAsync("Inspector");
        var approver = await Db.CreateUserAsync("Approver");
        var shipper = await Db.CreateUserAsync("Shipper");
        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 100, Pass);
        await Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 40);

        var results = await RunConcurrentAsync(i => (i % 4) switch
        {
            0 => Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 10, Pass),
            1 => Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, 5, Fail, reason: "동시 불합격"),
            2 => Db.ApproveShipmentAsync(TestDb.NewKey(), lot, approver, 10),
            _ => Db.ShipLotAsync(TestDb.NewKey(), lot, shipper, 10),
        });

        // 비즈니스 규칙 거절(52xxx)만 허용, 교착 상태(1205) 등 다른 오류는 없어야 한다
        Assert.All(results.Where(r => r.Error is not null),
            r => Assert.True(r.Error!.Number is 52001 or 52007, $"예상 밖 오류 {r.Error.Number}: {r.Error.Message}"));

        var q = await Db.GetQuantitiesAsync(lot);
        Assert.Equal(200, q.Uninspected + q.Passed + q.Failed + q.Held);
        Assert.True(q.Available >= 0, $"출하 가능 수량 음수: {q}");
        Assert.True(q.Reserved >= 0 && q.Shipped >= 0);
        await AssertLedgerMatchesQuantitiesAsync(lot);
    }
}
