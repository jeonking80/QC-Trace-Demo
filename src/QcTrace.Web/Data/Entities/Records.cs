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
    public const string Request = "Request";       // 검사 의뢰 (수량 변화 없음, Delta 0)
    public const string Inspect = "Inspect";
    public const string Correct = "Correct";
    public const string Approve = "Approve";
    public const string Ship = "Ship";
    public const string Reinspect = "Reinspect";   // 불합격 → 미검사 (재검사 요청)
    public const string Scrap = "Scrap";           // 불합격 → 폐기
}

public static class LedgerField
{
    public const string Uninspected = "Uninspected";
    public const string Passed = "Passed";
    public const string Failed = "Failed";
    public const string Held = "Held";
    public const string Shipped = "Shipped";
    public const string Reserved = "Reserved";
    public const string Scrapped = "Scrapped";
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

public static class RoleChangeKind
{
    public const string Add = "Add";
    public const string Remove = "Remove";
}

/// <summary>
/// 역할 변경 감사 기록(추가만). R5 독립 승인 등 규칙이 역할에 기대므로 누가·언제·무엇을·왜 바꿨는지 남긴다.
/// usp_SetUserRoles만 쓴다. 앱 계정은 이 테이블과 UserRoles에 직접 쓸 수 없다.
/// </summary>
public class RoleChange
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public string RoleName { get; set; } = "";
    public string Change { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// 검사 지연 알림(추가만). 검사 의뢰 후 48시간이 지나도 미검사가 남은 LOT을 처음 발견한 시각을 남긴다.
/// LOT당 한 번만 기록한다. 해소 여부는 저장하지 않고 현재 수량으로 판단한다(미검사 0 또는 상태 변경 → 해소).
/// </summary>
public class DelayAlert
{
    public long Id { get; set; }
    public int LotId { get; set; }
    public DateTime InspectionRequestedAt { get; set; }
    public int UninspectedAtDetection { get; set; }
    public DateTime DetectedAt { get; set; }
}
