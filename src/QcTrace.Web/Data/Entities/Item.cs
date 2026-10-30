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
