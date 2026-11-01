namespace QcTrace.Web.Data.Entities;

/// <summary>품목 기준정보</summary>
public class Item
{
    public int Id { get; set; }
    public string ItemCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Spec { get; set; }
    public bool IsActive { get; set; } = true;

    public List<InspectionItem> InspectionItems { get; set; } = [];
}

/// <summary>품목별 검사 항목 (규격 하한·상한)</summary>
public class InspectionItem
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public string Name { get; set; } = "";
    public decimal Lower { get; set; }
    public decimal Upper { get; set; }
    public string Unit { get; set; } = "";
    public int SortOrder { get; set; }

    public Item Item { get; set; } = null!;
}

/// <summary>
/// 불량 유형 기준정보(2026-11-01 사용자 결정: 목록에서 고르고 "기타"면 직접 입력).
/// 관리자가 기준정보 화면에서 추가·사용 중지한다. 기록이 참조하므로 지우지 않는다.
/// </summary>
public class DefectCode
{
    public const string Other = "ETC";

    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}
