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
