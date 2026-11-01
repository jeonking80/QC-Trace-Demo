using System.Net;
using QcTrace.IntegrationTests.Infrastructure;
using QcTrace.Web.Data;
using QcTrace.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace QcTrace.IntegrationTests.Schedule;

/// <summary>
/// 검사 일정 협의(2026-11-01 사용자 요청): 긴급·희망 기한 의뢰 → 검사자 회신(예정 시각·사유)
/// → 생산 담당 수락/거절 → 거절이면 관리자·승인권자가 최종 기한. 규칙은 SP가 검사한다.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ScheduleTests(SqlServerFixture fixture) : IDisposable
{
    private readonly Db _db = new(fixture);
    private readonly ApiFactory _factory = new(fixture);
    public void Dispose() => _factory.Dispose();

    private static DateTime In(double hours) => DateTime.UtcNow.AddHours(hours);

    private async Task<string?> StateAsync(int lot) => await _db.ScalarAsync("SELECT ScheduleState FROM Lots WHERE Id = @id", ("@id", lot)) as string;
    private async Task<DateTime?> DueAsync(int lot) => await _db.ScalarAsync("SELECT DueAt FROM Lots WHERE Id = @id", ("@id", lot)) as DateTime?;
    private async Task<int> EventsAsync(int lot, string kind) =>
        (int)(await _db.ScalarAsync("SELECT COUNT(*) FROM ScheduleEvents WHERE LotId = @id AND Kind = @k", ("@id", lot), ("@k", kind)))!;

    /// <summary>검사 의뢰까지 마친 LOT</summary>
    private async Task<int> RequestedLotAsync(string priority = "Normal", string? reason = null, DateTime? due = null)
    {
        var lot = await fixture.CreateLotAsync(10, status: "Registered");
        await _db.RequestInspection(Db.Key(), lot, "producer", priority, reason, due);
        return lot;
    }

    [Fact(DisplayName = "일정: 긴급 의뢰는 사유가 필요하고(52020), 지난 희망 기한도 거절(52020). 긴급·기한은 LOT과 일정 기록에 남는다")]
    public async Task Request_UrgentAndDue()
    {
        var a = await fixture.CreateLotAsync(10, status: "Registered");
        await SqlAssert.ThrowsAsync(52020, () => _db.RequestInspection(Db.Key(), a, "producer", "Urgent", "  ", null));
        await SqlAssert.ThrowsAsync(52020, () => _db.RequestInspection(Db.Key(), a, "producer", "Normal", null, In(-1)));
        Assert.Equal("Registered", await _db.StatusAsync(a));

        var due = In(24);
        await _db.RequestInspection(Db.Key(), a, "producer", "Urgent", "고객 납기 당김", due);
        Assert.Equal("Urgent", await _db.ScalarAsync("SELECT Priority FROM Lots WHERE Id = @id", ("@id", a)));
        Assert.Equal(due, (await DueAsync(a))!.Value, TimeSpan.FromMilliseconds(5));
        Assert.Equal(1, await EventsAsync(a, "Request"));
        Assert.Equal("검사 의뢰 (긴급)", await _db.ScalarAsync("SELECT Reason FROM Ledger WHERE LotId = @id AND Kind = N'Request'", ("@id", a)));

        // 보통·기한 없음은 일정 기록을 만들지 않는다(기존 의뢰와 같음)
        var b = await RequestedLotAsync();
        Assert.Equal(0, await EventsAsync(b, "Request"));
        Assert.Null(await DueAsync(b));
    }

    [Fact(DisplayName = "일정: 검사자 회신 → 생산 담당 수락이면 그 시각이 기한이 된다")]
    public async Task Propose_Accept()
    {
        var lot = await RequestedLotAsync();
        await _db.ApplyInspection(Db.Key(), lot, "inspector1", 4, "Pass");   // 부분 검사
        var proposed = In(30);

        await SqlAssert.ThrowsAsync(50403, () => _db.ProposeSchedule(Db.Key(), lot, "producer", proposed, "사유"));
        await SqlAssert.ThrowsAsync(52020, () => _db.ProposeSchedule(Db.Key(), lot, "inspector1", proposed, " "));
        await SqlAssert.ThrowsAsync(52020, () => _db.ProposeSchedule(Db.Key(), lot, "inspector1", In(-2), "지난 시각"));

        var key = Db.Key();
        await _db.ProposeSchedule(key, lot, "inspector1", proposed, "측정 지그 교정 중");
        Assert.True((await _db.ProposeSchedule(key, lot, "inspector1", proposed, "측정 지그 교정 중")).Replayed);   // R6
        Assert.Equal("Proposed", await StateAsync(lot));
        await SqlAssert.ThrowsAsync(52012, () => _db.ProposeSchedule(Db.Key(), lot, "inspector2", In(40), "또 회신"));

        await SqlAssert.ThrowsAsync(50403, () => _db.RespondSchedule(Db.Key(), lot, "inspector1", "Accept", null));
        await _db.RespondSchedule(Db.Key(), lot, "producer", "Accept", null);
        Assert.Equal("Accepted", await StateAsync(lot));
        Assert.Equal(proposed, (await DueAsync(lot))!.Value, TimeSpan.FromMilliseconds(5));
        await SqlAssert.ThrowsAsync(52012, () => _db.RespondSchedule(Db.Key(), lot, "producer", "Accept", null));   // 확인할 회신 없음

        // 수락된 뒤에는 다시 회신할 수 있다
        await _db.ProposeSchedule(Db.Key(), lot, "inspector2", In(50), "추가 지연");
        Assert.Equal("Proposed", await StateAsync(lot));
    }

    [Fact(DisplayName = "일정: 생산 담당이 거절(사유 필수)하면 결정권자에게 넘어가고, 관리자·승인권자만 최종 기한을 정한다")]
    public async Task Reject_Escalates_ToDecider()
    {
        var lot = await RequestedLotAsync(due: In(10));
        await SqlAssert.ThrowsAsync(52012, () => _db.DecideSchedule(Db.Key(), lot, "approver", In(5), "아직 거절 전"));
        await _db.ProposeSchedule(Db.Key(), lot, "inspector1", In(30), "장비 고장");

        await SqlAssert.ThrowsAsync(52020, () => _db.RespondSchedule(Db.Key(), lot, "producer", "Reject", ""));
        await _db.RespondSchedule(Db.Key(), lot, "producer", "Reject", "오늘 출하 필요");
        Assert.Equal("Escalated", await StateAsync(lot));
        Assert.Equal(In(10), (await DueAsync(lot))!.Value, TimeSpan.FromMinutes(1));   // 거절은 기한을 바꾸지 않는다
        await SqlAssert.ThrowsAsync(52012, () => _db.ProposeSchedule(Db.Key(), lot, "inspector1", In(20), "다시"));

        await SqlAssert.ThrowsAsync(50403, () => _db.DecideSchedule(Db.Key(), lot, "producer", In(5), "생산이 결정"));
        await SqlAssert.ThrowsAsync(50403, () => _db.DecideSchedule(Db.Key(), lot, "inspector1", In(5), "검사가 결정"));
        await SqlAssert.ThrowsAsync(52020, () => _db.DecideSchedule(Db.Key(), lot, "approver", In(5), " "));
        var final = In(6);
        await _db.DecideSchedule(Db.Key(), lot, "approver", final, "오후 6시까지로 확정");
        Assert.Equal("Decided", await StateAsync(lot));
        Assert.Equal(final, (await DueAsync(lot))!.Value, TimeSpan.FromMilliseconds(5));
        Assert.Equal(["Request", "Propose", "Reject", "Decide"],
            await KindsAsync(lot));

        // 관리자도 결정권자다
        var other = await RequestedLotAsync();
        await _db.ProposeSchedule(Db.Key(), other, "inspector2", In(30), "장비");
        await _db.RespondSchedule(Db.Key(), other, "producer", "Reject", "너무 늦음");
        await _db.DecideSchedule(Db.Key(), other, "admin", In(8), "관리자 결정");
        Assert.Equal("Decided", await StateAsync(other));
    }

    private async Task<string[]> KindsAsync(int lot)
    {
        await using var db = fixture.CreateDbContext();
        return await db.ScheduleEvents.Where(e => e.LotId == lot).OrderBy(e => e.Id).Select(e => e.Kind).ToArrayAsync();
    }

    [Fact(DisplayName = "일정: 미검사가 없거나 의뢰 전인 LOT은 회신할 수 없다(52012)")]
    public async Task Propose_NeedsRemaining()
    {
        var registered = await fixture.CreateLotAsync(5, status: "Registered");
        await SqlAssert.ThrowsAsync(52012, () => _db.ProposeSchedule(Db.Key(), registered, "inspector1", In(5), "의뢰 전"));
        var done = await RequestedLotAsync();
        await _db.ApplyInspection(Db.Key(), done, "inspector1", 10, "Pass");
        await SqlAssert.ThrowsAsync(52012, () => _db.ProposeSchedule(Db.Key(), done, "inspector1", In(5), "다 끝남"));
    }

    [Fact(DisplayName = "일정 기록은 추가만: 관리자 연결은 트리거가 막고(51000), 앱 계정은 직접 쓸 수 없다(229)")]
    public async Task Events_AppendOnly()
    {
        var lot = await RequestedLotAsync("Urgent", "긴급", In(5));
        await SqlAssert.ThrowsAsync(51000, () => _db.ScalarAsync("UPDATE ScheduleEvents SET Reason = N'조작' WHERE LotId = @id", ("@id", lot)));
        await SqlAssert.ThrowsAsync(51000, () => _db.ScalarAsync("DELETE ScheduleEvents WHERE LotId = @id", ("@id", lot)));
        await SqlAssert.ThrowsAsync(229, () => _db.ExecuteAsAppAsync($"INSERT INTO ScheduleEvents (LotId, Kind, ActorId) VALUES ({lot}, N'Decide', N'{_db.User("admin")}')"));
    }

    [Fact(DisplayName = "지연 판단: 기한이 있으면 그 기한(지나면 지연), 없으면 의뢰 후 48시간")]
    public async Task Delay_UsesDueAt()
    {
        var overdue = await RequestedLotAsync(due: In(0.01));
        var future = await RequestedLotAsync(due: In(100));
        var plain = await RequestedLotAsync();
        // 의뢰 시각을 과거로(관리자 연결): 50시간 전 의뢰
        foreach (var id in new[] { overdue, future, plain })
            await _db.ScalarAsync("UPDATE Lots SET InspectionRequestedAt = DATEADD(HOUR, -50, SYSUTCDATETIME()) WHERE Id = @id", ("@id", id));
        await _db.ScalarAsync("UPDATE Lots SET DueAt = DATEADD(HOUR, -1, SYSUTCDATETIME()) WHERE Id = @id", ("@id", overdue));

        await using var db = fixture.CreateDbContext();
        var delayed = (await QcQueries.DelayedLotsAsync(db, DateTime.UtcNow)).ToDictionary(d => d.LotId);
        Assert.Contains(overdue, delayed.Keys);
        Assert.DoesNotContain(future, delayed.Keys);   // 50시간 지났어도 기한이 남았으면 지연 아님
        Assert.Contains(plain, delayed.Keys);
        Assert.StartsWith("기한 1시간 초과", delayed[overdue].Describe(DateTime.UtcNow));
    }

    [Fact(DisplayName = "일정 화면: 결정 화면은 관리자·승인권자만, 회신 화면은 검사자만. 검사 목록은 긴급이 맨 위")]
    public async Task Screens()
    {
        foreach (var (user, code) in new[] { ("admin", 200), ("approver", 200), ("producer", 403), ("inspector1", 403), ("shipper", 403) })
            Assert.Equal(code, (int)(await _factory.ClientFor(user).GetAsync("/Schedule")).StatusCode);

        var normal = await RequestedLotAsync();
        var urgent = await RequestedLotAsync("Urgent", "화면 정렬 확인용 긴급", In(30));
        Assert.Equal(HttpStatusCode.OK, (await _factory.ClientFor("inspector1").GetAsync($"/Inspections/Schedule/{urgent}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("producer").GetAsync($"/Inspections/Schedule/{urgent}")).StatusCode);

        var html = await _factory.ClientFor("inspector1").GetStringAsync("/Inspections");
        var urgentNo = (string)(await _db.ScalarAsync("SELECT LotNo FROM Lots WHERE Id = @id", ("@id", urgent)))!;
        var normalNo = (string)(await _db.ScalarAsync("SELECT LotNo FROM Lots WHERE Id = @id", ("@id", normal)))!;
        Assert.True(html.IndexOf(urgentNo, StringComparison.Ordinal) < html.IndexOf(normalNo, StringComparison.Ordinal), "긴급 LOT이 먼저 나와야 한다");
        Assert.Contains("화면 정렬 확인용 긴급", html);

        // 생산 담당 화면에 회신 확인, 거절 뒤 결정 화면·대시보드에 결정 필요
        await _db.ProposeSchedule(Db.Key(), urgent, "inspector1", In(40), "화면 회신 사유");
        Assert.Contains("화면 회신 사유", await _factory.ClientFor("producer").GetStringAsync("/Lots"));
        await _db.RespondSchedule(Db.Key(), urgent, "producer", "Reject", "화면 거절 사유");
        Assert.Contains("화면 거절 사유", await _factory.ClientFor("approver").GetStringAsync("/Schedule"));
        Assert.Contains("일정 결정 필요", await _factory.ClientFor("admin").GetStringAsync("/api/dashboard"));
        var detail = await _factory.ClientFor("shipper").GetStringAsync($"/Trace/Lot/{urgentNo}");
        Assert.Contains("회신 거절 → 결정권자", detail);
        Assert.Contains("최종 기한을 정합니다", detail);
    }

    [Fact(DisplayName = "대시보드 검사 지연 카드 숫자와, 카드를 눌러 여는 LOT 목록(기한 기준 포함)이 같다")]
    public async Task DelayedCard_MatchesList()
    {
        var overdue = await RequestedLotAsync(due: In(0.01));
        await _db.ScalarAsync("UPDATE Lots SET DueAt = DATEADD(HOUR, -1, SYSUTCDATETIME()) WHERE Id = @id", ("@id", overdue));
        var future = await RequestedLotAsync(due: In(100));
        await _db.ScalarAsync("UPDATE Lots SET InspectionRequestedAt = DATEADD(HOUR, -60, SYSUTCDATETIME()) WHERE Id = @id", ("@id", future));

        var client = _factory.ClientFor("admin");
        var json = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("/api/dashboard")).RootElement;
        var count = json.GetProperty("delayed").GetProperty("count").GetInt32();
        var html = await client.GetStringAsync("/Trace?need=delayed");
        Assert.Contains($"· {count}건", html);
        string No(int id) => (string)_db.ScalarAsync("SELECT LotNo FROM Lots WHERE Id = @id", ("@id", id)).Result!;
        Assert.Contains(No(overdue), html);
        Assert.DoesNotContain(No(future), html);
    }

    [Fact(DisplayName = "업그레이드 전(V2 해시)에 처리된 검사 의뢰 키를 같은 내용으로 다시 보내면 재전송으로 인정한다(R6)")]
    public async Task LegacyRequestKey_Replays()
    {
        var lot = await RequestedLotAsync();
        var key = Db.Key();
        var legacy = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.Unicode.GetBytes($"RequestInspection|{lot}|{_db.User("producer")}")));
        await _db.ScalarAsync("INSERT INTO QcRequests (RequestKey, Operation, BodyHash, ResponseJson) VALUES (@k, N'RequestInspection', @h, N'{}')",
            ("@k", key), ("@h", legacy));
        Assert.True((await _db.RequestInspection(key, lot, "producer")).Replayed);
        await SqlAssert.ThrowsAsync(50409, () => _db.RequestInspection(key, lot, "producer", "Urgent", "다른 내용", null));
    }

    [Fact(DisplayName = "일정: 결정권자가 정한 기한은 생산 담당이 새 회신을 수락해도 바로 바뀌지 않고 결정권자가 다시 확인한다")]
    public async Task Decided_ChangeNeedsDeciderAgain()
    {
        var lot = await RequestedLotAsync();
        await _db.ProposeSchedule(Db.Key(), lot, "inspector1", In(30), "장비");
        await _db.RespondSchedule(Db.Key(), lot, "producer", "Reject", "늦음");
        var decided = In(8);
        await _db.DecideSchedule(Db.Key(), lot, "approver", decided, "8시간 뒤로 확정");

        await _db.ProposeSchedule(Db.Key(), lot, "inspector2", In(20), "추가 고장");
        var accepted = await _db.RespondSchedule(Db.Key(), lot, "producer", "Accept", null);
        Assert.Equal("Escalated", accepted.Response.GetProperty("scheduleState").GetString());
        Assert.Equal("Escalated", await StateAsync(lot));
        Assert.Equal(decided, (await DueAsync(lot))!.Value, TimeSpan.FromMilliseconds(5));   // 기한은 그대로

        var lotNo = (string)(await _db.ScalarAsync("SELECT LotNo FROM Lots WHERE Id = @id", ("@id", lot)))!;
        Assert.Contains("회신 수락 → 결정권자 확인", await _factory.ClientFor("producer").GetStringAsync($"/Trace/Lot/{lotNo}"));

        var final = In(12);
        await _db.DecideSchedule(Db.Key(), lot, "admin", final, "변경 승인");
        Assert.Equal("Decided", await StateAsync(lot));
        Assert.Equal(final, (await DueAsync(lot))!.Value, TimeSpan.FromMilliseconds(5));
    }

    [Fact(DisplayName = "메뉴 통합: 생산 담당만 가진 사용자는 'LOT 조회' 메뉴가 없고 LOT 화면에서 번호로 찾는다. 다른 역할은 메뉴가 있다")]
    public async Task ProducerMenu_Merged()
    {
        var lot = await fixture.CreateLotAsync(5);
        var lotNo = (string)(await _db.ScalarAsync("SELECT LotNo FROM Lots WHERE Id = @id", ("@id", lot)))!;
        var producerHtml = await _factory.ClientFor("producer").GetStringAsync("/Lots?q=" + lotNo[^8..]);
        Assert.DoesNotContain(">LOT 조회<", producerHtml);
        Assert.Contains(lotNo, producerHtml);
        Assert.Contains(">LOT 조회<", await _factory.ClientFor("inspector1").GetStringAsync("/Inspections"));
    }
}
