using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using QcTrace.IntegrationTests.Infrastructure;

namespace QcTrace.IntegrationTests.Api;

/// <summary>
/// 실제 앱 파이프라인(라우팅·역할 정책·SP·ApiExceptionHandler)으로 API를 호출해
/// SP 오류 번호가 HTTP 상태와 errorCode로 바뀌는지 확인한다.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class CommandApiTests(SqlServerFixture fixture) : IDisposable
{
    private readonly ApiFactory _factory = new(fixture);
    private readonly Db _db = new(fixture);

    public void Dispose() => _factory.Dispose();

    private static HttpRequestMessage Post(string url, object? body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body ?? new { }) };
        if (key != "") request.Headers.Add("Idempotency-Key", key ?? Db.Key());
        return request;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Response)> Send(HttpClient client, HttpRequestMessage request)
    {
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var body = text.Length > 0 && response.Content.Headers.ContentType?.MediaType?.Contains("json") == true
            ? JsonDocument.Parse(text).RootElement.Clone()
            : default;
        return (response.StatusCode, body, response);
    }

    private static string? ErrorCode(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("errorCode", out var c) ? c.GetString() : null;

    [Fact(DisplayName = "API 전체 흐름: 등록→의뢰→검사→승인→출하, 재전송은 Idempotent-Replayed: true")]
    public async Task FullFlow_ThroughApi()
    {
        var producer = _factory.ClientFor("producer");
        var reg = await Send(producer, Post("/api/lots", new { itemId = fixture.ItemId, producedQty = 40, producedOn = "2030-03-01", shift = "B조" }));
        Assert.Equal(HttpStatusCode.OK, reg.Status);
        var lot = reg.Body.GetProperty("lotId").GetInt32();
        Assert.StartsWith("LOT-20300301-", reg.Body.GetProperty("lotNo").GetString());

        Assert.Equal(HttpStatusCode.OK, (await Send(producer, Post($"/api/lots/{lot}/inspection-request", null))).Status);

        var inspector = _factory.ClientFor("inspector1");
        var key = Db.Key();
        var first = await Send(inspector, Post($"/api/lots/{lot}/inspections", new { qty = 40, verdict = "Pass" }, key));
        var again = await Send(inspector, Post($"/api/lots/{lot}/inspections", new { qty = 40, verdict = "Pass" }, key));
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal("false", first.Response.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal("true", again.Response.Headers.GetValues("Idempotent-Replayed").Single());

        var approve = await Send(_factory.ClientFor("approver"), Post($"/api/lots/{lot}/approvals", new { qty = 25 }));
        Assert.Equal(HttpStatusCode.OK, approve.Status);
        var ship = await Send(_factory.ClientFor("shipper"), Post($"/api/lots/{lot}/shipments", new { qty = 25, destination = "고객사 A" }));
        Assert.Equal(HttpStatusCode.OK, ship.Status);

        Assert.Equal(new Quantities(0, 40, 0, 0, 25, 0), await _db.QuantitiesAsync(lot));
        await _db.AssertLedgerMatchesAsync(lot);
    }

    [Fact(DisplayName = "Idempotency-Key 헤더가 없으면 400 idempotency_key_required, SP는 호출되지 않는다")]
    public async Task MissingIdempotencyKey_Returns400()
    {
        var lot = await fixture.CreateLotAsync(10);
        var r = await Send(_factory.ClientFor("inspector1"), Post($"/api/lots/{lot}/inspections", new { qty = 1, verdict = "Pass" }, key: ""));

        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.Equal("idempotency_key_required", ErrorCode(r.Body));
        Assert.Equal(new Quantities(10, 0, 0, 0, 0, 0), await _db.QuantitiesAsync(lot));
    }

    [Fact(DisplayName = "역할 정책: 검사자 API를 생산 담당이 부르면 403, 로그인 안 하면 401")]
    public async Task RolePolicy_Applies()
    {
        var lot = await fixture.CreateLotAsync(10);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(_factory.ClientFor("producer"), Post($"/api/lots/{lot}/inspections", new { qty = 1, verdict = "Pass" }))).Status);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(_factory.ClientFor("norole"), Post("/api/lots", new { itemId = fixture.ItemId, producedQty = 1, producedOn = "2030-01-01", shift = "A" }))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Send(_factory.CreateClient(), Post($"/api/lots/{lot}/shipments", new { qty = 1 }))).Status);
    }

    public static TheoryData<string, int, string> ErrorCases => new()
    {
        { "input",   400, "invalid_input" },
        { "notfound", 404, "not_found" },
        { "R6",      409, "R6" },
        { "state",   409, "invalid_state" },
        { "R1",      422, "R1" },
        { "R3",      422, "R3" },
        { "R4",      422, "R4" },
        { "R5",      422, "R5" },
        { "R7",      422, "R7" },
        { "dispose", 422, "dispose_over_failed" },
        { "R11",     422, "R11" },
        { "R11missing", 422, "R11" },
    };

    [Theory(DisplayName = "SP 오류 번호 → HTTP 상태·errorCode 매핑")]
    [MemberData(nameof(ErrorCases))]
    public async Task SqlErrors_MapToHttp(string scenario, int status, string code)
    {
        var inspector = _factory.ClientFor("inspector1");
        var lot = await fixture.CreateLotAsync(10);
        HttpRequestMessage request;
        HttpClient client = inspector;

        switch (scenario)
        {
            case "input":
                request = Post($"/api/lots/{lot}/inspections", new { qty = 0, verdict = "Pass" });
                break;
            case "notfound":
                request = Post("/api/lots/999999/inspections", new { qty = 1, verdict = "Pass" });
                break;
            case "R6":
                var key = Db.Key();
                await Send(inspector, Post($"/api/lots/{lot}/inspections", new { qty = 1, verdict = "Pass" }, key));
                request = Post($"/api/lots/{lot}/inspections", new { qty = 2, verdict = "Pass" }, key);
                break;
            case "state":
                var registered = await fixture.CreateLotAsync(10, "Registered");
                request = Post($"/api/lots/{registered}/inspections", new { qty = 1, verdict = "Pass" });
                break;
            case "R1":
                client = _factory.ClientFor("approver");
                request = Post($"/api/lots/{lot}/approvals", new { qty = 1 });
                break;
            case "R3":
                var original = await _db.ApplyInspection(Db.Key(), lot, "inspector1", 2, "Pass");
                var originalId = original.Response.GetProperty("inspectionId").GetInt64();
                request = Post($"/api/lots/{lot}/inspections",
                    new { qty = 3, verdict = "Fail", reason = "정정", correctsInspectionId = originalId });
                break;
            case "R4":
                request = Post($"/api/lots/{lot}/inspections", new { qty = 1, verdict = "Fail" });
                break;
            case "R5":
                await _db.ApplyInspection(Db.Key(), lot, "inspector_approver", 5, "Pass");
                client = _factory.ClientFor("inspector_approver");
                request = Post($"/api/lots/{lot}/approvals", new { qty = 1 });
                break;
            case "R7":
                await _db.ApplyInspection(Db.Key(), lot, "inspector1", 5, "Pass");
                await _db.ApproveShipment(Db.Key(), lot, "approver", 3);
                client = _factory.ClientFor("shipper");
                request = Post($"/api/lots/{lot}/shipments", new { qty = 4, destination = "X" });
                break;
            case "dispose":
                await _db.ApplyInspection(Db.Key(), lot, "inspector1", 2, "Fail", reason: "불량");
                request = Post($"/api/lots/{lot}/dispositions", new { qty = 3, action = "Scrap", reason = "폐기" });
                break;
            case "R11":
                var (specItem, diameter, _) = await fixture.CreateItemWithSpecsAsync();
                var specLot = await fixture.CreateLotAsync(10, itemId: specItem);
                request = Post($"/api/lots/{specLot}/inspections", new
                {
                    qty = 1, verdict = "Pass",
                    measuredJson = System.Text.Json.JsonSerializer.Serialize(new[] { new { inspectionItemId = diameter, value = 10.2 } })
                });
                break;
            case "R11missing":
                var (missingItem, _, _) = await fixture.CreateItemWithSpecsAsync();
                var missingLot = await fixture.CreateLotAsync(10, itemId: missingItem);
                request = Post($"/api/lots/{missingLot}/inspections", new { qty = 1, verdict = "Pass" });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        var r = await Send(client, request);
        Assert.Equal(status, (int)r.Status);
        Assert.Equal(code, ErrorCode(r.Body));
        Assert.Equal("application/problem+json", r.Response.Content.Headers.ContentType?.MediaType);
    }
}
