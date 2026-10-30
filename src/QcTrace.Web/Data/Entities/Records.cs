namespace QcTrace.Web.Data.Entities;

// 아래 테이블은 추가만 허용한다. UPDATE·DELETE는 DB 트리거가 거부한다.

public static class Verdict
{
    public const string Pass = "Pass";
    public const string Fail = "Fail";
    public const string Hold = "Hold";
}

/// <summary>검사 실적. 정정은 새 행을 추가하고 CorrectsInspectionId로 원 행을 가리킨다.</summary>
public class Inspection
{
    public long Id { get; set; }
    public int LotId { get; set; }
    public string InspectorId { get; set; } = "";
    public int Qty { get; set; }
    public string Verdict { get; set; } = "";
    public string? MeasuredJson { get; set; }
    public string? Reason { get; set; }
    public long? CorrectsInspectionId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class ApprovalDecision
{
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

/// <summary>출하 승인</summary>
public class ShipmentApproval
{
    public long Id { get; set; }
    public int LotId { get; set; }
    public string ApproverId { get; set; } = "";
    public int ApprovedQty { get; set; }
    public string Decision { get; set; } = "";
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>출하 실적</summary>
public class Shipment
{
    public long Id { get; set; }
    public int LotId { get; set; }
    public string ShipperId { get; set; } = "";
    public int Qty { get; set; }
    public string Destination { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public static class LedgerKind
{
    public const string Produce = "Produce";
    public const string Inspect = "Inspect";
    public const string Correct = "Correct";
    public const string Approve = "Approve";
    public const string Ship = "Ship";
}

public static class LedgerField
{
    public const string Uninspected = "Uninspected";
    public const string Passed = "Passed";
    public const string Failed = "Failed";
    public const string Held = "Held";
    public const string Shipped = "Shipped";
    public const string Reserved = "Reserved";
}

/// <summary>불변 원장. LotQuantities의 각 열은 이 테이블의 DeltaField별 Delta 합으로 다시 계산할 수 있다.</summary>
public class LedgerEntry
{
    public long Id { get; set; }
    public int LotId { get; set; }
    public string Kind { get; set; } = "";
    public string DeltaField { get; set; } = "";
    public int Delta { get; set; }
    public string ActorId { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? SourceTable { get; set; }
    public long? SourceId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>멱등 요청 저장. 같은 키 재전송 시 저장된 응답을 돌려준다.</summary>
public class QcRequest
{
    public long Id { get; set; }
    public string RequestKey { get; set; } = "";
    public string Operation { get; set; } = "";
    public string BodyHash { get; set; } = "";
    public string? ResponseJson { get; set; }
    public DateTime CreatedAt { get; set; }
}
