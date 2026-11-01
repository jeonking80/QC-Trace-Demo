using Microsoft.Data.SqlClient;

namespace QcTrace.Reconcile;

/// <summary>수량 7가지. 원장(DeltaField)과 LotQuantities 열 이름이 같다.</summary>
public sealed record Quantities(int Uninspected, int Passed, int Failed, int Held, int Scrapped, int Shipped, int Reserved)
{
    public static readonly string[] Fields = ["Uninspected", "Passed", "Failed", "Held", "Scrapped", "Shipped", "Reserved"];
    public static readonly Dictionary<string, string> Labels = new()
    {
        ["Uninspected"] = "미검사", ["Passed"] = "합격", ["Failed"] = "불합격", ["Held"] = "보류",
        ["Scrapped"] = "폐기", ["Shipped"] = "출하", ["Reserved"] = "예약",
    };

    public int this[string field] => field switch
    {
        "Uninspected" => Uninspected, "Passed" => Passed, "Failed" => Failed, "Held" => Held,
        "Scrapped" => Scrapped, "Shipped" => Shipped, "Reserved" => Reserved,
        _ => throw new InvalidOperationException($"알 수 없는 수량 필드 {field}"),
    };
}

public sealed class Lot
{
    public required int Id { get; init; }
    public required string LotNo { get; init; }
    public required int ProducedQty { get; init; }
    public Quantities Current { get; set; } = new(0, 0, 0, 0, 0, 0, 0);
    public bool HasQuantityRow { get; set; }
}

public sealed record LedgerRow(long Id, int LotId, string Kind, string DeltaField, int Delta, string? SourceTable, long? SourceId);
public sealed record InspectionRow(long Id, int LotId, int Qty, string Verdict, long? CorrectsInspectionId);
public sealed record ApprovalRow(long Id, int LotId, int ApprovedQty, string Decision);
public sealed record ShipmentRow(long Id, int LotId, int Qty);

public sealed class Snapshot
{
    public Dictionary<int, Lot> Lots { get; } = new();
    public ILookup<int, LedgerRow> Ledger { get; private set; } = null!;
    public ILookup<int, InspectionRow> Inspections { get; private set; } = null!;
    public Dictionary<int, int> ShipmentQty { get; } = new();
    public Dictionary<long, InspectionRow> InspectionById { get; private set; } = new();
    public Dictionary<long, ApprovalRow> ApprovalById { get; } = new();
    public Dictionary<long, ShipmentRow> ShipmentById { get; } = new();
    public int LedgerCount { get; private set; }
    public int InspectionCount { get; private set; }
    public int ApprovalCount { get; private set; }
    public int ShipmentCount { get; private set; }

    /// <summary>한 번의 스냅샷 트랜잭션에서 읽는다(읽는 도중 업무가 진행돼도 시점이 어긋나지 않게).</summary>
    public static async Task<Snapshot> LoadAsync(string connectionString)
    {
        var s = new Snapshot();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);

        async Task ReadAsync(string sql, Action<SqlDataReader> row)
        {
            await using var command = new SqlCommand(sql, connection, tx);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) row(reader);
        }

        await ReadAsync("SELECT Id, LotNo, ProducedQty FROM dbo.Lots", r =>
            s.Lots[r.GetInt32(0)] = new Lot { Id = r.GetInt32(0), LotNo = r.GetString(1), ProducedQty = r.GetInt32(2) });

        await ReadAsync("SELECT LotId, Uninspected, Passed, Failed, Held, Scrapped, Shipped, Reserved FROM dbo.LotQuantities", r =>
        {
            if (!s.Lots.TryGetValue(r.GetInt32(0), out var lot)) return;
            lot.Current = new Quantities(r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7));
            lot.HasQuantityRow = true;
        });

        var ledger = new List<LedgerRow>();
        await ReadAsync("SELECT Id, LotId, Kind, DeltaField, Delta, SourceTable, SourceId FROM dbo.Ledger", r =>
            ledger.Add(new LedgerRow(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetInt32(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : Convert.ToInt64(r.GetValue(6)))));
        s.Ledger = ledger.ToLookup(l => l.LotId);
        s.LedgerCount = ledger.Count;

        var inspections = new List<InspectionRow>();
        await ReadAsync("SELECT Id, LotId, Qty, Verdict, CorrectsInspectionId FROM dbo.Inspections", r =>
            inspections.Add(new InspectionRow(Convert.ToInt64(r.GetValue(0)), r.GetInt32(1), r.GetInt32(2), r.GetString(3),
                r.IsDBNull(4) ? null : Convert.ToInt64(r.GetValue(4)))));
        s.Inspections = inspections.ToLookup(i => i.LotId);
        s.InspectionById = inspections.ToDictionary(i => i.Id);
        s.InspectionCount = inspections.Count;

        await ReadAsync("SELECT LotId, Qty, Id FROM dbo.Shipments", r =>
        {
            s.ShipmentById[Convert.ToInt64(r.GetValue(2))] = new ShipmentRow(Convert.ToInt64(r.GetValue(2)), r.GetInt32(0), r.GetInt32(1));
            s.ShipmentQty[r.GetInt32(0)] = s.ShipmentQty.GetValueOrDefault(r.GetInt32(0)) + r.GetInt32(1);
            s.ShipmentCount++;
        });
        await ReadAsync("SELECT Id, LotId, ApprovedQty, Decision FROM dbo.ShipmentApprovals", r =>
        {
            var id = Convert.ToInt64(r.GetValue(0));
            s.ApprovalById[id] = new ApprovalRow(id, r.GetInt32(1), r.GetInt32(2), r.GetString(3));
            s.ApprovalCount++;
        });

        await tx.CommitAsync();
        return s;
    }
}

public sealed record LotResult(Lot Lot, int Overage, List<string> Problems);

public static class Reconciler
{
    public static LotResult Check(Lot lot, Snapshot data)
    {
        var problems = new List<string>();
        var q = lot.Current;
        var ledger = data.Ledger[lot.Id].ToList();

        if (!lot.HasQuantityRow) problems.Add("LotQuantities 행 없음");

        // 1. 현재 수량 = 원장 합 (필드마다)
        foreach (var field in Quantities.Fields)
        {
            var sum = ledger.Where(l => l.DeltaField == field).Sum(l => l.Delta);
            if (sum != q[field]) problems.Add($"{Quantities.Labels[field]} 현재 {q[field]} ≠ 원장 {sum}");
        }

        // 2. 생산 원장 = 등록 생산 수량
        var produced = ledger.Where(l => l.Kind == "Produce").Sum(l => l.Delta);
        if (produced != lot.ProducedQty) problems.Add($"생산 원장 {produced} ≠ 생산 수량 {lot.ProducedQty}");

        // 3. 음수 없음, 출하 + 예약 ≤ 합격
        if (Quantities.Fields.Any(f => q[f] < 0)) problems.Add("음수 수량");
        if (q.Shipped + q.Reserved > q.Passed) problems.Add($"출하 {q.Shipped} + 예약 {q.Reserved} > 합격 {q.Passed}");

        // 4. 출하 실적 테이블 합 = 출하 수량
        var shipped = data.ShipmentQty.GetValueOrDefault(lot.Id);
        if (shipped != q.Shipped) problems.Add($"출하 실적 합 {shipped} ≠ 출하 {q.Shipped}");

        // 5. 생산 등식: 미검사 + 합격 + 불합격 + 보류 + 폐기 − 생산 = R9 초과 보류
        //    R9 초과분 = 정정이 아닌 검사 행마다 (검사 수량 − 그 행 때문에 미검사에서 빠진 수량)
        var appliedBySource = ledger
            .Where(l => l.Kind == "Inspect" && l.DeltaField == "Uninspected" && l.SourceTable == "Inspections" && l.SourceId is not null)
            .GroupBy(l => l.SourceId!.Value)
            .ToDictionary(g => g.Key, g => -g.Sum(l => l.Delta));
        var overage = data.Inspections[lot.Id]
            .Where(i => i.CorrectsInspectionId is null)
            .Sum(i => i.Qty - appliedBySource.GetValueOrDefault(i.Id));
        var diff = q.Uninspected + q.Passed + q.Failed + q.Held + q.Scrapped - lot.ProducedQty;
        if (diff != overage) problems.Add($"생산 등식 차이 {diff} ≠ R9 초과 {overage}");

        // 6. 원장 행마다 근거가 되는 원본 행이 있고, 그 행과 수량·방향이 맞는다
        problems.AddRange(SourceProblems(lot, ledger, data));

        return new LotResult(lot, overage, problems);
    }

    /// <summary>종류별로 허용되는 출처 테이블과 수량 이동. (필드, 부호): +1 증가, -1 감소, 0 변화 없음</summary>
    private static readonly Dictionary<string, (string Table, (string Field, int Sign)[] Moves)> LotSourced = new()
    {
        ["Produce"] = ("Lots", [("Uninspected", 1)]),
        ["Request"] = ("Lots", [("Uninspected", 0)]),
        ["Reinspect"] = ("Lots", [("Failed", -1), ("Uninspected", 1)]),
        ["Scrap"] = ("Lots", [("Failed", -1), ("Scrapped", 1)]),
    };

    private static string VerdictField(string verdict) => verdict switch
    {
        "Pass" => "Passed", "Fail" => "Failed", "Hold" => "Held", _ => "?",
    };

    private static IEnumerable<string> SourceProblems(Lot lot, List<LedgerRow> ledger, Snapshot data)
    {
        foreach (var row in ledger)
        {
            if (row.SourceTable is null || row.SourceId is null)
            {
                yield return $"원장 #{row.Id} 출처 없음";
                continue;
            }
            switch (row.Kind)
            {
                case "Produce" or "Request" or "Reinspect" or "Scrap":
                    var rule = LotSourced[row.Kind];
                    if (row.SourceTable != rule.Table || row.SourceId != lot.Id)
                        yield return $"원장 #{row.Id} 출처 {row.SourceTable} {row.SourceId}가 이 LOT이 아님";
                    else if (!rule.Moves.Any(m => m.Field == row.DeltaField && Math.Sign(row.Delta) == m.Sign))
                        yield return $"원장 #{row.Id} {row.Kind}에 맞지 않는 이동 {row.DeltaField} {row.Delta:+#;-#;0}";
                    break;
                case "Inspect" or "Correct":
                    if (row.SourceTable != "Inspections" || !data.InspectionById.TryGetValue(row.SourceId.Value, out var i) || i.LotId != lot.Id)
                        yield return $"원장 #{row.Id} 출처 검사 행 없음({row.SourceTable} {row.SourceId})";
                    break;
                case "Approve":
                    if (row.SourceTable != "ShipmentApprovals" || !data.ApprovalById.TryGetValue(row.SourceId.Value, out var a) || a.LotId != lot.Id)
                        yield return $"원장 #{row.Id} 출처 승인 행 없음({row.SourceTable} {row.SourceId})";
                    break;
                case "Ship":
                    if (row.SourceTable != "Shipments" || !data.ShipmentById.TryGetValue(row.SourceId.Value, out var sh) || sh.LotId != lot.Id)
                        yield return $"원장 #{row.Id} 출처 출하 행 없음({row.SourceTable} {row.SourceId})";
                    break;
                default:
                    yield return $"원장 #{row.Id} 알 수 없는 종류 {row.Kind}";
                    break;
            }
        }

        // 검사 행: 그 행에서 나온 원장의 증가분 합 = 검사 수량, 증가는 판정 필드(원 검사는 R9 초과분 보류 포함)에만
        foreach (var g in ledger.Where(l => l.SourceTable == "Inspections" && l.SourceId is not null).GroupBy(l => l.SourceId!.Value))
        {
            if (!data.InspectionById.TryGetValue(g.Key, out var i)) continue;
            var target = VerdictField(i.Verdict);
            var increase = g.Where(l => l.Delta > 0).Sum(l => l.Delta);
            if (increase != i.Qty) yield return $"검사 #{i.Id} 수량 {i.Qty} ≠ 원장 증가 {increase}";
            if (g.Any(l => l.Delta > 0 && l.DeltaField != target && !(i.CorrectsInspectionId is null && l.DeltaField == "Held")))
                yield return $"검사 #{i.Id} 판정 {i.Verdict}과 다른 필드가 늘어남";
            if (i.CorrectsInspectionId is null && g.Any(l => l.Delta < 0 && l.DeltaField != "Uninspected"))
                yield return $"검사 #{i.Id} 원 검사인데 미검사 외 필드가 줄어듦";
        }
        // 승인 행: 승인은 예약을 승인 수량만큼, 반려는 원장 없음
        foreach (var a in data.ApprovalById.Values.Where(a => a.LotId == lot.Id))
        {
            var reserved = ledger.Where(l => l.SourceTable == "ShipmentApprovals" && l.SourceId == a.Id).Sum(l => l.Delta);
            var expected = a.Decision == "Approved" ? a.ApprovedQty : 0;
            if (reserved != expected) yield return $"승인 #{a.Id} 수량 {expected} ≠ 원장 예약 {reserved}";
        }
        // 출하 행: 출하 +수량, 예약 −수량
        foreach (var sh in data.ShipmentById.Values.Where(x => x.LotId == lot.Id))
        {
            var rows = ledger.Where(l => l.SourceTable == "Shipments" && l.SourceId == sh.Id).ToList();
            if (rows.Where(l => l.DeltaField == "Shipped").Sum(l => l.Delta) != sh.Qty || rows.Where(l => l.DeltaField == "Reserved").Sum(l => l.Delta) != -sh.Qty)
                yield return $"출하 #{sh.Id} 수량 {sh.Qty}과 원장이 다름";
        }
    }
}
