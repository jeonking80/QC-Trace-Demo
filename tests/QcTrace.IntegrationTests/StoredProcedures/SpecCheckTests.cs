using QcTrace.IntegrationTests.Infrastructure;

namespace QcTrace.IntegrationTests.StoredProcedures;

/// <summary>R11: 측정값이 규격을 벗어나면 합격으로 저장하지 않는다(2026-11-01 사용자 결정).</summary>
[Collection(SqlServerCollection.Name)]
public class SpecCheckTests(SqlServerFixture fixture)
{
    private readonly Db _db = new(fixture);

    private static string Json(params object[] entries) => System.Text.Json.JsonSerializer.Serialize(entries);

    [Fact(DisplayName = "R11 규격을 벗어난 측정값으로 합격 입력은 52014로 거절, 수량·이력은 그대로")]
    public async Task OutOfSpecPass_IsRejected()
    {
        var (item, d, l) = await fixture.CreateItemWithSpecsAsync();
        var lot = await fixture.CreateLotAsync(10, itemId: item);

        var ex = await SqlAssert.ThrowsAsync(52014, () => _db.ApplyInspection(Db.Key(), lot, "inspector1", 5, "Pass",
            measuredJson: Json(new { inspectionItemId = d, value = 10.2 }, new { inspectionItemId = l, value = 50.0 })));

        Assert.Contains("외경", ex.Message);
        Assert.Equal(new Quantities(10, 0, 0, 0, 0, 0), await _db.QuantitiesAsync(lot));
        Assert.Equal(0, await _db.CountAsync("Inspections", lot));
    }

    [Fact(DisplayName = "R11 같은 측정값이라도 불합격·보류로는 저장된다")]
    public async Task OutOfSpecFailOrHold_IsAccepted()
    {
        var (item, d, _) = await fixture.CreateItemWithSpecsAsync();
        var lot = await fixture.CreateLotAsync(10, itemId: item);
        var json = Json(new { inspectionItemId = d, value = 141.5 });

        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 3, "Fail", reason: "외경 상한 초과", measuredJson: json);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 2, "Hold", reason: "재측정 필요", measuredJson: json);

        Assert.Equal(new Quantities(5, 0, 3, 2, 0, 0), await _db.QuantitiesAsync(lot));
        await _db.AssertLedgerMatchesAsync(lot);
    }

    [Fact(DisplayName = "R11 규격 경계값(하한·상한 포함)은 합격으로 저장된다")]
    public async Task InSpecBoundary_PassAccepted()
    {
        var (item, d, l) = await fixture.CreateItemWithSpecsAsync();
        var lot = await fixture.CreateLotAsync(10, itemId: item);

        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 1, "Pass",
            measuredJson: Json(new { inspectionItemId = d, value = 139 }, new { inspectionItemId = l, value = 50.5 }));
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 1, "Pass",
            measuredJson: Json(new { inspectionItemId = d, value = 141 }, new { inspectionItemId = l, value = 49.5 }));

        Assert.Equal(new Quantities(8, 2, 0, 0, 0, 0), await _db.QuantitiesAsync(lot));
    }

    public static TheoryData<string, string?> MissingCases => new()
    {
        { "측정값 생략", null },
        { "빈 배열", "[]" },
        { "일부 항목 누락", "[{\"name\":\"외경\",\"value\":140}]" },
        { "null 값", "[{\"name\":\"외경\",\"value\":140},{\"name\":\"길이\",\"value\":null}]" },
        { "빈 문자열 값", "[{\"name\":\"외경\",\"value\":140},{\"name\":\"길이\",\"value\":\"\"}]" },
    };

    [Theory(DisplayName = "R11 합격은 모든 검사 항목 측정이 필요하다(52015), 수량은 그대로")]
    [MemberData(nameof(MissingCases))]
    public async Task MissingMeasurement_PassRejected(string _, string? json)
    {
        var (item, _, _) = await fixture.CreateItemWithSpecsAsync();
        var lot = await fixture.CreateLotAsync(10, itemId: item);

        var ex = await SqlAssert.ThrowsAsync(52015, () => _db.ApplyInspection(Db.Key(), lot, "inspector1", 1, "Pass", measuredJson: json));

        Assert.Contains("길이", ex.Message);
        Assert.Equal(new Quantities(10, 0, 0, 0, 0, 0), await _db.QuantitiesAsync(lot));
    }

    [Fact(DisplayName = "R11 불합격·보류는 측정값이 없어도 저장되고, 검사 항목이 없는 품목은 측정값 없이 합격할 수 있다")]
    public async Task MissingAllowedForFailHoldAndItemsWithoutSpecs()
    {
        var (item, _, _) = await fixture.CreateItemWithSpecsAsync();
        var lot = await fixture.CreateLotAsync(10, itemId: item);
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 1, "Fail", reason: "외관 불량");
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 1, "Hold", reason: "측정기 고장", measuredJson: "[]");
        Assert.Equal(new Quantities(8, 0, 1, 1, 0, 0), await _db.QuantitiesAsync(lot));

        var plain = await fixture.CreateLotAsync(5); // 검사 항목 없는 테스트 품목
        await _db.ApplyInspection(Db.Key(), plain, "inspector1", 5, "Pass");
        Assert.Equal(new Quantities(0, 5, 0, 0, 0, 0), await _db.QuantitiesAsync(plain));
    }

    [Fact(DisplayName = "R11 항목 Id 없이 이름으로 보내도 검사하고, 보낸 lower·upper는 무시한다")]
    public async Task NameMatchAndClientSpecIgnored()
    {
        var (item, _, _) = await fixture.CreateItemWithSpecsAsync();
        var lot = await fixture.CreateLotAsync(10, itemId: item);

        await SqlAssert.ThrowsAsync(52014, () => _db.ApplyInspection(Db.Key(), lot, "inspector1", 1, "Pass",
            measuredJson: Json(new { name = "길이", value = 51, lower = 0, upper = 100 })));
    }

    [Fact(DisplayName = "R11 숫자가 아닌 값, 다른 품목의 항목, 배열이 아닌 JSON은 50400")]
    public async Task InvalidMeasurements_Rejected()
    {
        var (item, d, _) = await fixture.CreateItemWithSpecsAsync();
        var (_, otherD, _) = await fixture.CreateItemWithSpecsAsync();
        var lot = await fixture.CreateLotAsync(10, itemId: item);

        await SqlAssert.ThrowsAsync(50400, () => _db.ApplyInspection(Db.Key(), lot, "inspector1", 1, "Pass",
            measuredJson: Json(new { inspectionItemId = d, value = "abc" })));
        await SqlAssert.ThrowsAsync(50400, () => _db.ApplyInspection(Db.Key(), lot, "inspector1", 1, "Fail", reason: "x",
            measuredJson: Json(new { inspectionItemId = otherD, value = 140 })));
        await SqlAssert.ThrowsAsync(50400, () => _db.ApplyInspection(Db.Key(), lot, "inspector1", 1, "Pass",
            measuredJson: "{\"value\": 140}"));
    }

    [Fact(DisplayName = "R11 정정으로 합격으로 바꿀 때도 규격 밖 측정값이면 거절")]
    public async Task CorrectionToPass_Checked()
    {
        var (item, d, l) = await fixture.CreateItemWithSpecsAsync();
        var lot = await fixture.CreateLotAsync(10, itemId: item);
        var fail = await _db.ApplyInspection(Db.Key(), lot, "inspector1", 4, "Fail", reason: "외경 불량",
            measuredJson: Json(new { inspectionItemId = d, value = 142 }));
        var failId = fail.Response.GetProperty("inspectionId").GetInt64();

        await SqlAssert.ThrowsAsync(52014, () => _db.ApplyInspection(Db.Key(), lot, "inspector1", 4, "Pass",
            reason: "판정 정정", correctsInspectionId: failId, measuredJson: Json(new { inspectionItemId = d, value = 142 })));
        // 원 검사 측정값을 쓰지 않는다: 측정값 없이, 또는 일부만 넣고 합격으로 정정하면 거절
        await SqlAssert.ThrowsAsync(52015, () => _db.ApplyInspection(Db.Key(), lot, "inspector1", 4, "Pass",
            reason: "판정 정정", correctsInspectionId: failId));
        await SqlAssert.ThrowsAsync(52015, () => _db.ApplyInspection(Db.Key(), lot, "inspector1", 4, "Pass",
            reason: "판정 정정", correctsInspectionId: failId, measuredJson: Json(new { inspectionItemId = d, value = 140.1 })));
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 4, "Pass",
            reason: "재측정 결과 정상", correctsInspectionId: failId,
            measuredJson: Json(new { inspectionItemId = d, value = 140.1 }, new { inspectionItemId = l, value = 50 }));

        Assert.Equal(new Quantities(6, 4, 0, 0, 0, 0), await _db.QuantitiesAsync(lot));
    }
}
