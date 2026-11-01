using QcTrace.Web.Data.Entities;

namespace QcTrace.Web.Services;

/// <summary>
/// 원장 행을 사람이 읽는 한 줄로 바꾼다. 원장 데이터는 그대로 두고 화면 표시만 바꾼다(집 PC 피드백 B5).
/// 한 업무 동작(같은 종류·같은 근거 행·같은 시각)이 원장에서는 −/+ 여러 행이므로, 묶어서 "불합격 → 폐기 15개"처럼 한 줄로 쓴다.
/// </summary>
public static class LedgerText
{
    public sealed record Line(long Id, DateTime CreatedAt, string LotNo, string Kind, string DeltaField, int Delta,
        string Actor, string? Reason, string? SourceTable, long? SourceId);

    public sealed record Event(long FirstId, DateTime CreatedAt, string LotNo, string Kind, string KindLabel, string Text,
        string Actor, string? Reason, string Source, IReadOnlyList<Line> Lines);

    public static readonly (string Value, string Label)[] Kinds =
    [
        (LedgerKind.Produce, "생산"), (LedgerKind.Request, "검사 의뢰"), (LedgerKind.Inspect, "검사"), (LedgerKind.Correct, "정정"),
        (LedgerKind.Reinspect, "재검사"), (LedgerKind.Scrap, "폐기"), (LedgerKind.Approve, "출하 승인"), (LedgerKind.Ship, "출하"),
    ];

    public static string KindLabel(string kind) => Kinds.FirstOrDefault(k => k.Value == kind).Label ?? kind;

    public static string FieldLabel(string field) => field switch
    {
        "Uninspected" => "미검사",
        "Passed" => "합격",
        "Failed" => "불합격",
        "Held" => "보류",
        "Scrapped" => "폐기",
        "Shipped" => "출하",
        "Reserved" => "출하 예약",
        _ => field,
    };

    public static string SourceLabel(string? table, long? id) => table switch
    {
        null => "",
        "Lots" => "LOT",
        "Inspections" => $"검사 기록 #{id}",
        "ShipmentApprovals" => $"승인 기록 #{id}",
        "Shipments" => $"출하 기록 #{id}",
        _ => $"{table} #{id}",
    };

    /// <summary>원장 행을 동작 단위로 묶는다. 순서는 시각 오름차순(오래된 것부터).</summary>
    public static List<Event> Group(IEnumerable<Line> lines) =>
        lines
            .GroupBy(l => (l.LotNo, l.Kind, l.SourceTable, l.SourceId, l.CreatedAt))
            .Select(g =>
            {
                var rows = g.OrderBy(l => l.Id).ToList();
                var first = rows[0];
                return new Event(first.Id, first.CreatedAt, first.LotNo, first.Kind, KindLabel(first.Kind), Describe(first.Kind, rows),
                    first.Actor, first.Reason, SourceLabel(first.SourceTable, first.SourceId), rows);
            })
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.FirstId)
            .ToList();

    /// <summary>"미검사 → 합격 60개", "생산 수량 초과분 보류 5개(R9)" 같은 문장</summary>
    public static string Describe(string kind, IReadOnlyList<Line> rows)
    {
        if (kind == LedgerKind.Request) return "검사 의뢰 (수량 변화 없음)";
        if (kind == LedgerKind.Produce) return $"생산 등록 {rows.Where(r => r.Delta > 0).Sum(r => r.Delta)}개 → 미검사";

        var minus = rows.Where(r => r.Delta < 0).GroupBy(r => r.DeltaField).Select(g => (Field: g.Key, Qty: -g.Sum(r => r.Delta))).ToList();
        var plus = rows.Where(r => r.Delta > 0).GroupBy(r => r.DeltaField).Select(g => (Field: g.Key, Qty: g.Sum(r => r.Delta))).ToList();
        var parts = new List<string>();

        // 줄어든 필드 하나 → 같은 수량만큼 늘어난 필드 하나는 "A → B n개"로 잇는다
        foreach (var m in minus.ToList())
        {
            var match = plus.FirstOrDefault(p => p.Qty == m.Qty);
            if (match.Field is null) match = plus.FirstOrDefault(p => p.Field != "Held");
            if (match.Field is null) match = plus.FirstOrDefault();
            if (match.Field is null) continue;
            var qty = Math.Min(m.Qty, match.Qty);
            parts.Add($"{FieldLabel(m.Field)} → {FieldLabel(match.Field)} {qty}개");
            minus.Remove(m);
            plus.Remove(match);
            if (match.Qty > qty) plus.Add((match.Field, match.Qty - qty));
            if (m.Qty > qty) minus.Add((m.Field, m.Qty - qty));
        }
        foreach (var p in plus)
        {
            parts.Add(kind == LedgerKind.Inspect && p.Field == "Held"
                ? $"생산 수량 초과분 보류 {p.Qty}개 (R9)"
                : $"{FieldLabel(p.Field)} +{p.Qty}개");
        }
        foreach (var m in minus) parts.Add($"{FieldLabel(m.Field)} −{m.Qty}개");
        return parts.Count == 0 ? "수량 변화 없음" : string.Join(", ", parts);
    }
}
