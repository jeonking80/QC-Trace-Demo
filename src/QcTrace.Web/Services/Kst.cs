namespace QcTrace.Web.Services;

/// <summary>DB 시각은 UTC다. 화면에는 한국 시간으로 보여준다.</summary>
public static class Kst
{
    private static readonly TimeZoneInfo Zone = FindZone();

    public static DateTime FromUtc(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    /// <summary>CSV용: 초까지</summary>
    public static string FormatSeconds(DateTime utc) => FromUtc(utc).ToString("yyyy-MM-dd HH:mm:ss");

    public static string Format(DateTime? utc) => utc is null ? "" : FromUtc(utc.Value).ToString("yyyy-MM-dd HH:mm");

    /// <summary>한국 날짜 00:00을 UTC 시각으로. 기간 필터의 시작(포함)·끝(다음 날, 제외)에 쓴다.</summary>
    public static DateTime StartOfDayUtc(DateOnly kstDate) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(kstDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), Zone);

    public static DateOnly Today => DateOnly.FromDateTime(FromUtc(DateTime.UtcNow));

    private static TimeZoneInfo FindZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.CreateCustomTimeZone("KST", TimeSpan.FromHours(9), "KST", "KST"); }
    }
}
