using Microsoft.EntityFrameworkCore;
using QcTrace.IntegrationTests.Infrastructure;
using QcTrace.Web.Data;
using QcTrace.Web.Services;

namespace QcTrace.IntegrationTests.Admin;

/// <summary>기준정보 CSV: 전부 검사 후 오류가 하나라도 있으면 저장하지 않음. 앱 계정으로 실행.</summary>
[Collection(SqlServerCollection.Name)]
public class MasterDataTests(SqlServerFixture fixture)
{
    private QcTraceDbContext NewDb() =>
        new(new DbContextOptionsBuilder<QcTraceDbContext>().UseSqlServer(fixture.AppConnectionString).Options);

    private static string Code() => "C" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact(DisplayName = "품목·검사 항목 CSV: 추가 후 같은 키로 다시 올리면 수정된다(따옴표·쉼표·BOM 처리)")]
    public async Task ImportThenUpdate()
    {
        var code = Code();
        await using var db = NewDb();
        var md = new MasterData(db);

        var items = await md.ImportItemsAsync($"﻿ItemCode,Name,Spec,IsActive\r\n{code},\"브래킷, 소형\",\"\"\"A\"\" 타입\",1\r\n");
        Assert.True(items.Ok, string.Join("; ", items.Errors));
        Assert.Equal((1, 0), (items.Added, items.Updated));

        var specs = await md.ImportInspectionItemsAsync($"ItemCode,Name,Lower,Upper,Unit,SortOrder\n{code},두께,1.95,2.05,mm,1\n{code},폭,29.5,30.5,mm,2\n");
        Assert.True(specs.Ok, string.Join("; ", specs.Errors));
        Assert.Equal(2, specs.Added);

        var again = await md.ImportInspectionItemsAsync($"ItemCode,Name,Lower,Upper,Unit,SortOrder\n{code},두께,1.9,2.1,mm,1\n{code},폭,29.5,30.5,mm,2\n");
        Assert.Equal((0, 1), (again.Added, again.Updated));

        await using var check = NewDb();
        var item = await check.Items.Include(i => i.InspectionItems).SingleAsync(i => i.ItemCode == code);
        Assert.Equal("브래킷, 소형", item.Name);
        Assert.Equal("\"A\" 타입", item.Spec);
        Assert.Equal(2.1m, item.InspectionItems.Single(x => x.Name == "두께").Upper);
    }

    [Fact(DisplayName = "CSV 한 줄이라도 오류면 아무것도 저장하지 않고 줄 번호와 이유를 알려준다")]
    public async Task AllOrNothing()
    {
        var ok = Code();
        var dup = Code();
        await using var db = NewDb();
        var md = new MasterData(db);

        var result = await md.ImportItemsAsync($"ItemCode,Name,Spec,IsActive\n{ok},정상,,1\n{dup},중복1,,1\n{dup},중복2,,1\n,이름만,,1\nX1,잘못된 값,,maybe\n");

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.StartsWith("4행:") && e.Contains("두 번"));
        Assert.Contains(result.Errors, e => e.StartsWith("5행:") && e.Contains("코드"));
        Assert.Contains(result.Errors, e => e.StartsWith("6행:") && e.Contains("IsActive"));
        await using var check = NewDb();
        Assert.False(await check.Items.AnyAsync(i => i.ItemCode == ok));
    }

    [Fact(DisplayName = "검사 항목 CSV: 없는 품목, 하한>상한, 숫자 아님, 소수 5자리, 머리글 틀림은 거절")]
    public async Task InspectionItemErrors()
    {
        var code = Code();
        await using var db = NewDb();
        var md = new MasterData(db);
        await md.ImportItemsAsync($"ItemCode,Name,Spec,IsActive\n{code},품목,,1\n");

        var result = await md.ImportInspectionItemsAsync(
            $"ItemCode,Name,Lower,Upper,Unit,SortOrder\nNOPE,a,1,2,mm,1\n{code},b,3,2,mm,1\n{code},c,abc,2,mm,1\n{code},d,1.00001,2,mm,1\n");
        Assert.Equal(4, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.StartsWith("2행:") && e.Contains("NOPE"));
        Assert.Contains(result.Errors, e => e.StartsWith("3행:") && e.Contains("하한이 상한보다"));
        Assert.Contains(result.Errors, e => e.StartsWith("4행:") && e.Contains("숫자가 아닙니다"));
        Assert.Contains(result.Errors, e => e.StartsWith("5행:") && e.Contains("소수 4자리"));

        var header = await md.ImportInspectionItemsAsync("Code,Name\nx,y\n");
        Assert.Single(header.Errors);
        Assert.Contains("머리글", header.Errors[0]);
    }

    [Fact(DisplayName = "CSV 내려받기: UTF-8 BOM, 따옴표 처리, 수식으로 시작하는 값 무력화")]
    public void CsvWrite_EscapesAndNeutralizesFormulas()
    {
        var bytes = Csv.Write(["a", "b", "c"], [new object?[] { "=1+1", "x,\"y\"", -5 }]);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = System.Text.Encoding.UTF8.GetString(bytes[3..]);
        Assert.Equal("a,b,c\r\n'=1+1,\"x,\"\"y\"\"\",-5\r\n", text);
        var back = Csv.Read(text);
        Assert.Equal(["'=1+1", "x,\"y\"", "-5"], back[1]);
    }
}
