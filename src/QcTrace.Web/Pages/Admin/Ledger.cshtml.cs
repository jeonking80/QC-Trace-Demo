using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;
using QcTrace.Web.Services;

namespace QcTrace.Web.Pages.Admin;

/// <summary>
/// 이력 원장. LOT·작업자·종류·기간(한국 날짜)으로 거르고 CSV로 내려받는다.
/// 원장은 추가만 되는 테이블이라 화면은 조회만 한다. 역할 변경 이력(RoleChanges)도 여기서 본다.
/// </summary>
public class LedgerModel(QcTraceDbContext db) : QcPageModel
{
    public const int ScreenLimit = 300;
    public const int CsvLimit = 100_000;

    [BindProperty(SupportsGet = true)] public string? View { get; set; }
    [BindProperty(SupportsGet = true)] public string? LotNo { get; set; }
    [BindProperty(SupportsGet = true)] public string? ActorId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Kind { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    /// <summary>raw: 원장 행 그대로(−/+ 행 각각). 기본은 동작 한 건을 한 줄로(집 PC 피드백 B5)</summary>
    [BindProperty(SupportsGet = true)] public string? Show { get; set; }
    public bool IsRaw => Show == "raw";
    public List<LedgerText.Event> Events { get; private set; } = [];
    /// <summary>LOT을 골라 보면 일이 일어난 순서(오래된 것부터), 아니면 최근 것부터</summary>
    public bool OldestFirst => !string.IsNullOrWhiteSpace(LotNo);

    public sealed record Row(long Id, DateTime CreatedAt, string LotNo, string Kind, string DeltaField, int Delta,
        string Actor, string Reason, string? SourceTable, long? SourceId);
    public sealed record RoleRow(long Id, DateTime CreatedAt, string User, string RoleName, string Change, string Actor, string Reason);

    public List<Row> Rows { get; private set; } = [];
    public List<RoleRow> RoleRows { get; private set; } = [];
    public int TotalCount { get; private set; }
    public List<SelectListItem> ActorOptions { get; private set; } = [];

    public static readonly (string Value, string Label)[] Kinds = LedgerText.Kinds;

    public static string KindLabel(string kind) => LedgerText.KindLabel(kind);

    public bool IsRoleView => View == "roles";

    public async Task<IActionResult> OnGetAsync()
    {
        if (From is not null && To is not null && From > To)
        {
            ModelState.AddModelError("", "시작일이 종료일보다 늦습니다.");
        }
        ActorOptions = await db.Users.AsNoTracking().OrderBy(u => u.UserName)
            .Select(u => new SelectListItem($"{u.DisplayName} ({u.UserName})", u.Id)).ToListAsync();

        if (IsRoleView)
        {
            var roles = RoleQuery();
            TotalCount = await roles.CountAsync();
            RoleRows = await roles.Take(ScreenLimit).ToListAsync();
        }
        else
        {
            var query = Query();
            TotalCount = await query.CountAsync();
            Rows = await query.Take(ScreenLimit).ToListAsync();
            Events = LedgerText.Group(Rows.Select(r => new LedgerText.Line(r.Id, r.CreatedAt, r.LotNo, r.Kind, r.DeltaField, r.Delta,
                r.Actor, r.Reason, r.SourceTable, r.SourceId)));
            if (!OldestFirst) Events.Reverse();
            if (IsRaw && OldestFirst) Rows.Reverse();
        }
        return Page();
    }

    public async Task<IActionResult> OnGetCsvAsync()
    {
        var stamp = Kst.FromUtc(DateTime.UtcNow).ToString("yyyyMMdd-HHmm");
        if (IsRoleView)
        {
            var roles = await RoleQuery().Take(CsvLimit).ToListAsync();
            var bytes = Csv.Write(["Id", "시각(KST)", "사용자", "역할", "변경", "변경자", "사유"],
                roles.Select(r => new object?[] { r.Id, Kst.FormatSeconds(r.CreatedAt), r.User, r.RoleName, r.Change == RoleChangeKind.Add ? "추가" : "제거", r.Actor, r.Reason }));
            return File(bytes, "text/csv; charset=utf-8", $"role-changes-{stamp}.csv");
        }

        var rows = await Query().Take(CsvLimit).ToListAsync();
        var csv = Csv.Write(["Id", "시각(KST)", "LOT", "종류", "수량 항목", "증감", "작업자", "사유", "원본 테이블", "원본 Id"],
            rows.Select(r => new object?[] { r.Id, Kst.FormatSeconds(r.CreatedAt), r.LotNo, KindLabel(r.Kind), r.DeltaField, r.Delta, r.Actor, r.Reason, r.SourceTable, r.SourceId }));
        return File(csv, "text/csv; charset=utf-8", $"ledger-{stamp}.csv");
    }

    private (DateTime? From, DateTime? To) UtcRange() =>
        (From is null ? null : Kst.StartOfDayUtc(From.Value), To is null ? null : Kst.StartOfDayUtc(To.Value.AddDays(1)));

    private IQueryable<Row> Query()
    {
        var (from, to) = UtcRange();
        var query =
            from l in db.Ledger.AsNoTracking()
            join lot in db.Lots on l.LotId equals lot.Id
            join u in db.Users on l.ActorId equals u.Id
            select new { l, lot.LotNo, u.DisplayName, u.UserName };

        if (!string.IsNullOrWhiteSpace(LotNo)) query = query.Where(x => x.LotNo.Contains(LotNo.Trim()));
        if (!string.IsNullOrEmpty(ActorId)) query = query.Where(x => x.l.ActorId == ActorId);
        if (!string.IsNullOrEmpty(Kind)) query = query.Where(x => x.l.Kind == Kind);
        if (from is not null) query = query.Where(x => x.l.CreatedAt >= from);
        if (to is not null) query = query.Where(x => x.l.CreatedAt < to);

        return query.OrderByDescending(x => x.l.CreatedAt).ThenByDescending(x => x.l.Id)
            .Select(x => new Row(x.l.Id, x.l.CreatedAt, x.LotNo, x.l.Kind, x.l.DeltaField, x.l.Delta,
                x.DisplayName + " (" + x.UserName + ")", x.l.Reason, x.l.SourceTable, x.l.SourceId));
    }

    private IQueryable<RoleRow> RoleQuery()
    {
        var (from, to) = UtcRange();
        var query =
            from r in db.RoleChanges.AsNoTracking()
            join u in db.Users on r.UserId equals u.Id
            join a in db.Users on r.ActorId equals a.Id
            select new { r, User = u.DisplayName + " (" + u.UserName + ")", Actor = a.DisplayName + " (" + a.UserName + ")" };

        // 작업자 필터는 바뀐 사용자 또는 바꾼 사람 어느 쪽이든 맞으면 보여준다
        if (!string.IsNullOrEmpty(ActorId)) query = query.Where(x => x.r.ActorId == ActorId || x.r.UserId == ActorId);
        if (from is not null) query = query.Where(x => x.r.CreatedAt >= from);
        if (to is not null) query = query.Where(x => x.r.CreatedAt < to);

        return query.OrderByDescending(x => x.r.CreatedAt).ThenByDescending(x => x.r.Id)
            .Select(x => new RoleRow(x.r.Id, x.r.CreatedAt, x.User, x.r.RoleName, x.r.Change, x.Actor, x.r.Reason));
    }
}
