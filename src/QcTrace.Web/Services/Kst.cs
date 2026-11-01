namespace QcTrace.Web.Services;

/// <summary>DB 시각은 UTC다. 화면에는 한국 시간으로 보여준다.</summary>
public static class Kst
{
    private static readonly TimeZoneInfo Zone = FindZone();

    public static DateTime FromUtc(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    public static string Format(DateTime? utc) => utc is null ? "" : FromUtc(utc.Value).ToString("yyyy-MM-dd HH:mm");

    public static DateOnly Today => DateOnly.FromDateTime(FromUtc(DateTime.UtcNow));

    private static TimeZoneInfo FindZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.CreateCustomTimeZone("KST", TimeSpan.FromHours(9), "KST", "KST"); }
    }
}
