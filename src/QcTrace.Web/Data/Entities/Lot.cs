namespace QcTrace.Web.Data.Entities;

public static class LotStatus
{
    public const string Registered = "Registered";   // 생산 등록
    public const string Waiting = "Waiting";         // 검사 대기
    public const string Inspecting = "Inspecting";   // 부분 검사
    public const string Inspected = "Inspected";     // 검사 완료
    public const string Shipped = "Shipped";         // 출하 완료

    public static readonly string[] All = [Registered, Waiting, Inspecting, Inspected, Shipped];
}

public static class LotPriority
{
    public const string Normal = "Normal";
    public const string Urgent = "Urgent";
}

public static class ScheduleState
{
    public const string Proposed = "Proposed";
    public const string Accepted = "Accepted";
    public const string Escalated = "Escalated";
    public const string Decided = "Decided";
    public static readonly string[] All = [Proposed, Accepted, Escalated, Decided];
}

/// <summary>생산 LOT 현재 상태</summary>
public class Lot
{
    public int Id { get; set; }
    public string LotNo { get; set; } = "";
    public int ItemId { get; set; }
    public int ProducedQty { get; set; }
    public DateOnly ProducedOn { get; set; }
    public string Shift { get; set; } = "";
    public string Status { get; set; } = LotStatus.Registered;
    public string CreatedById { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? InspectionRequestedAt { get; set; }

    // 검사 일정(2026-11-01 사용자 요청). 바뀔 때마다 ScheduleEvents에 추가로 남는다.
    /// <summary>Normal(보통) 또는 Urgent(긴급, 사유 필수)</summary>
    public string Priority { get; set; } = LotPriority.Normal;
    public string? PriorityReason { get; set; }
    /// <summary>지금 기준이 되는 검사 완료 기한(의뢰 때 희망 기한 → 수락·결정으로 바뀜). 없으면 48시간 지연 기준만 쓴다.</summary>
    public DateTime? DueAt { get; set; }
    /// <summary>검사자 일정 회신 단계: null(없음), Proposed(확인 대기), Accepted(수락), Escalated(거절 → 결정권자), Decided(결정권자 확정)</summary>
    public string? ScheduleState { get; set; }
    /// <summary>검사자가 회신한(아직 확인 전) 완료 예정 시각</summary>
    public DateTime? ProposedDueAt { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public Item Item { get; set; } = null!;
    public LotQuantity? Quantity { get; set; }
}

/// <summary>
/// LOT별 수량 지표 (조회용). Ledger 합과 항상 일치해야 한다.
/// 생산 수량 = Uninspected + Passed + Failed + Held + Scrapped, 출하 가능 = Passed - Shipped - Reserved
/// </summary>
public class LotQuantity
{
    public int LotId { get; set; }
    public int Uninspected { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Held { get; set; }
    public int Shipped { get; set; }
    public int Reserved { get; set; }
    /// <summary>불합격 처리에서 폐기로 확정한 수량</summary>
    public int Scrapped { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Lot Lot { get; set; } = null!;
}
