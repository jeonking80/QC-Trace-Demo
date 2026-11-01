using Microsoft.Data.SqlClient;
using QcTrace.Reconcile;

// QC Trace 독립 대조 (PLAN 7장)
// 앱의 계산 코드·EF 모델·DB 뷰(vw_LotReconciliation)를 쓰지 않는다. 원본 테이블의 행을 그대로 읽어 C#으로 다시 센다.
// 종료 코드: 0 = 전 LOT 일치, 1 = 불일치 있음, 2 = 실행 오류(연결 실패 등)
//
//   dotnet run --project tools/Reconcile                       # docker compose의 DB(localhost:1433)
//   dotnet run --project tools/Reconcile -- "<연결 문자열>" --all
//   QCTRACE_RECONCILE_SQL="<연결 문자열>" dotnet run --project tools/Reconcile

const string DefaultConnection =
    "Server=localhost,1433;Database=QcTrace;User Id=sa;Password=QcTrace!Dev2026;TrustServerCertificate=True;Encrypt=True;Connect Timeout=10";

var showAll = args.Contains("--all");
var connectionString = args.FirstOrDefault(a => !a.StartsWith("--"))
    ?? Environment.GetEnvironmentVariable("QCTRACE_RECONCILE_SQL")
    ?? DefaultConnection;

try
{
    var data = await Snapshot.LoadAsync(connectionString);
    var results = data.Lots.Values.OrderBy(l => l.LotNo).Select(lot => Reconciler.Check(lot, data)).ToList();
    var bad = results.Where(r => r.Problems.Count > 0).ToList();

    Console.WriteLine($"QC Trace 독립 대조  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine($"원본 행: LOT {data.Lots.Count}, 원장 {data.LedgerCount}, 검사 {data.InspectionCount}, 출하 승인 {data.ApprovalCount}, 출하 {data.ShipmentCount}");
    Console.WriteLine();
    Console.WriteLine($"{"LOT",-20} {"생산",5} {"미검사",6} {"합격",5} {"불합격",6} {"보류",5} {"폐기",5} {"출하",5} {"예약",5} {"R9초과",6}  결과");
    foreach (var r in showAll ? results : bad)
    {
        var q = r.Lot.Current;
        Console.WriteLine($"{r.Lot.LotNo,-20} {r.Lot.ProducedQty,5} {q.Uninspected,6} {q.Passed,5} {q.Failed,6} {q.Held,5} {q.Scrapped,5} {q.Shipped,5} {q.Reserved,5} {r.Overage,6}  "
            + (r.Problems.Count == 0 ? "일치" : "불일치: " + string.Join(" / ", r.Problems)));
    }
    if (!showAll && bad.Count == 0) Console.WriteLine("(불일치 LOT 없음. 전체 목록은 --all)");
    Console.WriteLine();
    Console.WriteLine($"결과: LOT {results.Count}건 중 불일치 {bad.Count}건, R9 초과 보류 {results.Count(r => r.Overage > 0)}건");
    return bad.Count == 0 ? 0 : 1;
}
catch (Exception ex) when (ex is SqlException or InvalidOperationException)
{
    Console.Error.WriteLine($"대조 실행 실패: {ex.Message}");
    return 2;
}
