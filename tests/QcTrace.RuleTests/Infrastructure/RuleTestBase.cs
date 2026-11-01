using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace QcTrace.RuleTests.Infrastructure;

[Collection(SqlCollection.Name)]
public abstract class RuleTestBase(SqlServerFixture fx)
{
    protected TestDb Db => fx.Db;

    protected const string Pass = "Pass";
    protected const string Fail = "Fail";
    protected const string Hold = "Hold";

    /// <summary>호출이 SqlException으로 실패하고, 그 번호가 기대값인지 확인한다.</summary>
    protected static async Task<SqlException> AssertSqlErrorAsync(int expectedNumber, Func<Task> call)
    {
        var ex = await Assert.ThrowsAsync<SqlException>(call);
        Assert.True(ex.Number == expectedNumber,
            $"SQL 오류 번호 기대 {expectedNumber}, 실제 {ex.Number}: {ex.Message}");
        return ex;
    }

    /// <summary>LotQuantities 각 열이 Ledger의 같은 DeltaField 합과 같은지 (6장 설계 원칙)</summary>
    protected async Task AssertLedgerMatchesQuantitiesAsync(int lotId)
    {
        var q = await Db.GetQuantitiesAsync(lotId);
        var sums = await Db.GetLedgerSumsAsync(lotId);
        Assert.Equal(sums, q);
    }

    protected static int Int(JsonElement json, string name) => json.GetProperty(name).GetInt32();

    protected static int QtyOf(SpResult r, string field) =>
        r.Json.GetProperty("quantities").GetProperty(field).GetInt32();

    /// <summary>검사 대기 LOT을 만들고 qty만큼 합격 처리한다.</summary>
    protected async Task<(int LotId, string InspectorId)> CreatePassedLotAsync(int producedQty, int passQty)
    {
        var lot = await Db.CreateWaitingLotAsync(producedQty);
        var inspector = await Db.CreateUserAsync("Inspector");
        await Db.ApplyInspectionAsync(TestDb.NewKey(), lot, inspector, passQty, Pass);
        return (lot, inspector);
    }
}
