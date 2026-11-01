using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;

namespace QcTrace.Web.Services;

/// <summary>
/// 검사 일정(긴급·기한·회신) 화면 문구. 규칙 검사는 SP가 하고, 여기는 보여주는 말만 만든다.
/// </summary>
public static class Schedule
{
    /// <summary>남은 시간: "D-3", "D-1", "5시간 남음", "40분 남음", "2시간 초과", "D+2 초과"</summary>
    public static string Remaining(DateTime dueUtc, DateTime nowUtc)
    {
        var left = dueUtc - nowUtc;
        if (left >= TimeSpan.Zero)
        {
            var days = (Kst.FromUtc(dueUtc).Date - Kst.FromUtc(nowUtc).Date).Days;
            if (left.TotalHours >= 24 && days >= 1) return $"D-{days}";
            if (left.TotalHours >= 1) return $"{(int)left.TotalHours}시간 남음";
            return $"{Math.Max(1, (int)left.TotalMinutes)}분 남음";
        }
        var over = -left;
        if (over.TotalHours >= 24) return $"D+{(int)over.TotalDays} 초과";
        if (over.TotalHours >= 1) return $"{(int)over.TotalHours}시간 초과";
        return $"{Math.Max(1, (int)over.TotalMinutes)}분 초과";
    }

    /// <summary>기한까지 남은 정도에 따른 배지 색: 초과(빨강), 24시간 이내(노랑), 그 외(회색)</summary>
    public static string DueCss(DateTime dueUtc, DateTime nowUtc) =>
        dueUtc < nowUtc ? "bg-danger" : dueUtc - nowUtc <= TimeSpan.FromHours(24) ? "bg-warning text-dark" : "bg-secondary";

    public static string StateLabel(string? state) => state switch
    {
        ScheduleState.Proposed => "회신 확인 대기",
        ScheduleState.Accepted => "일정 수락됨",
        ScheduleState.Escalated => "결정권자 결정 대기",
        ScheduleState.Decided => "결정권자 확정",
        _ => "",
    };

    public static string EventLabel(string kind) => kind switch
    {
        ScheduleEventKind.Request => "의뢰 조건",
        ScheduleEventKind.Propose => "검사자 일정 회신",
        ScheduleEventKind.Accept => "회신 수락",
        ScheduleEventKind.Reject => "회신 거절 → 결정권자",
        ScheduleEventKind.Decide => "최종 기한 결정",
        _ => kind,
    };

    /// <summary>검사자 목록 정렬: 긴급 → 기한 빠른 순(기한 없는 것은 뒤) → 의뢰 오래된 순</summary>
    public static IOrderedEnumerable<Lot> WorkOrder(IEnumerable<Lot> lots) =>
        lots.OrderBy(l => l.Priority == LotPriority.Urgent ? 0 : 1)
            .ThenBy(l => l.DueAt ?? DateTime.MaxValue)
            .ThenBy(l => l.InspectionRequestedAt);

    public sealed record HistoryRow(DateTime CreatedAt, string Kind, string? Priority, DateTime? DueAt, string? Reason, string Actor);

    /// <summary>LOT의 일정 협의 기록(오래된 순)</summary>
    public static Task<List<HistoryRow>> HistoryAsync(QcTraceDbContext db, int lotId) =>
        (from e in db.ScheduleEvents.AsNoTracking()
         join u in db.Users on e.ActorId equals u.Id
         where e.LotId == lotId
         orderby e.CreatedAt, e.Id
         select new HistoryRow(e.CreatedAt, e.Kind, e.Priority, e.DueAt, e.Reason, u.DisplayName + " (" + u.UserName + ")"))
        .ToListAsync();

    /// <summary>화면의 datetime-local 입력(한국 시간)을 UTC로</summary>
    public static DateTime? KstInputToUtc(DateTime? kstLocal) => kstLocal is { } v
        ? Kst.ToUtc(v)
        : null;
}
