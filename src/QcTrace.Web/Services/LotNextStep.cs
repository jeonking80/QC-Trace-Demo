using QcTrace.Web.Data.Entities;
using QcTrace.Web.Data.Seed;

namespace QcTrace.Web.Services;

/// <summary>LOT의 지금 상태에서 다음에 누가 무엇을 해야 하는지(집 PC 피드백 B4). LOT 상세와 작업 화면 안내가 같이 쓴다.</summary>
public static class LotNextStep
{
    public sealed record Step(string Role, string RoleLabel, string Text, string Path);

    public static string RoleLabel(string role) => role switch
    {
        Roles.Producer => "생산 담당",
        Roles.Inspector => "검사자",
        Roles.Approver => "승인권자",
        Roles.Shipper => "출하 담당",
        Roles.Admin => "관리자",
        _ => role,
    };

    public static List<Step> For(Lot lot, LotQuantity? q)
    {
        var steps = new List<Step>();
        if (lot.Status == LotStatus.Registered)
        {
            steps.Add(new(Roles.Producer, RoleLabel(Roles.Producer), "검사를 의뢰해야 검사자가 검사할 수 있습니다.", "/Lots?status=Registered"));
            return steps;
        }
        if (q is null) return steps;
        if (q.Uninspected > 0)
            steps.Add(new(Roles.Inspector, RoleLabel(Roles.Inspector), $"남은 미검사 {q.Uninspected}개를 검사합니다.", $"/Inspections/Input/{lot.Id}"));
        if (q.Failed > 0)
            steps.Add(new(Roles.Inspector, RoleLabel(Roles.Inspector), $"불합격 {q.Failed}개를 재검사 또는 폐기로 처리합니다(사유 필수).", $"/Inspections/Dispose/{lot.Id}"));
        if (q.Held > 0)
            steps.Add(new(Roles.Inspector, RoleLabel(Roles.Inspector), $"보류 {q.Held}개는 다시 판정(정정)해야 합니다.", "/Inspections"));
        var available = q.Passed - q.Shipped - q.Reserved;
        if (available > 0)
            steps.Add(new(Roles.Approver, RoleLabel(Roles.Approver), $"출하 가능 {available}개의 출하를 승인합니다. 이 LOT을 검사한 사람은 승인할 수 없습니다(R5).", "/Approvals"));
        if (q.Reserved > 0)
            steps.Add(new(Roles.Shipper, RoleLabel(Roles.Shipper), $"승인된 {q.Reserved}개를 출하합니다.", "/Shipments"));
        return steps;
    }
}
