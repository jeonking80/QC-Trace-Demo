namespace QcTrace.Web.Data.Sql;

/// <summary>Data/Sql 아래 포함 리소스 SQL 파일을 읽는다. 경로 예: "V1/002_usp_ApplyInspection.sql"</summary>
public static class SqlScripts
{
    public static string Read(string relativePath)
    {
        var suffix = ".Data.Sql." + relativePath.Replace('/', '.');
        var assembly = typeof(SqlScripts).Assembly;
        var name = assembly.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"포함 리소스 SQL이 없습니다: {relativePath}");
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
