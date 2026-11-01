using QcTrace.Web.Services;

namespace QcTrace.IntegrationTests.Screens;

/// <summary>원장 행을 한 줄·한글로 묶어 표시(집 PC 피드백 B5). DB 없이 문장만 확인한다.</summary>
public class LedgerTextTests
{
    private static readonly DateTime At = new(2026, 11, 1, 1, 0, 0, DateTimeKind.Utc);

    private static LedgerText.Line L(long id, string kind, string field, int delta, string table = "Inspections", long src = 7) =>
        new(id, At, "LOT-1", kind, field, delta, "검사자 1", "사유", table, src);

    [Fact(DisplayName = "원장 표시: 폐기 −/+ 두 행은 '불합격 → 폐기 15개' 한 줄")]
    public void Scrap_IsOneLine()
    {
        var e = Assert.Single(LedgerText.Group([L(1, "Scrap", "Failed", -15, "Lots", 3), L(2, "Scrap", "Scrapped", 15, "Lots", 3)]));
        Assert.Equal("폐기", e.KindLabel);
        Assert.Equal("불합격 → 폐기 15개", e.Text);
        Assert.Equal("LOT", e.Source);
    }

    [Theory(DisplayName = "원장 표시: 검사·R9 초과·출하·승인·생산·의뢰 문장")]
    [InlineData("Inspect", "Uninspected:-60,Passed:60", "미검사 → 합격 60개")]
    [InlineData("Inspect", "Uninspected:-2,Passed:2,Held:3", "미검사 → 합격 2개, 생산 수량 초과분 보류 3개 (R9)")]
    [InlineData("Inspect", "Uninspected:-2,Held:2,Held:3", "미검사 → 보류 2개, 생산 수량 초과분 보류 3개 (R9)")]
    [InlineData("Inspect", "Held:5", "생산 수량 초과분 보류 5개 (R9)")]
    [InlineData("Correct", "Failed:-5,Passed:5", "불합격 → 합격 5개")]
    [InlineData("Reinspect", "Failed:-20,Uninspected:20", "불합격 → 미검사 20개")]
    [InlineData("Ship", "Reserved:-95,Shipped:95", "출하 예약 → 출하 95개")]
    [InlineData("Approve", "Reserved:95", "출하 예약 +95개")]
    [InlineData("Produce", "Uninspected:100", "생산 등록 100개 → 미검사")]
    [InlineData("Request", "Uninspected:0", "검사 의뢰 (수량 변화 없음)")]
    public void Sentences(string kind, string moves, string expected)
    {
        var lines = moves.Split(',').Select((m, i) => L(i + 1, kind, m.Split(':')[0], int.Parse(m.Split(':')[1]))).ToList();
        Assert.Equal(expected, Assert.Single(LedgerText.Group(lines)).Text);
    }

    [Fact(DisplayName = "원장 표시: 다른 동작은 따로 묶이고 시간 순으로 정렬된다")]
    public void Groups_AreSeparated_AndOrdered()
    {
        var later = At.AddMinutes(5);
        var events = LedgerText.Group([
            L(3, "Ship", "Reserved", -5, "Shipments", 2) with { CreatedAt = later },
            L(4, "Ship", "Shipped", 5, "Shipments", 2) with { CreatedAt = later },
            L(1, "Inspect", "Uninspected", -5), L(2, "Inspect", "Passed", 5),
        ]);
        Assert.Equal(["검사", "출하"], events.Select(e => e.KindLabel).ToArray());
    }
}
