using System.Text;

namespace QcTrace.Web.Services;

/// <summary>
/// RFC 4180 CSV 쓰기·읽기. 내려받기는 엑셀에서 한글이 깨지지 않게 UTF-8 BOM을 붙인다.
/// 외부 라이브러리 없이 따옴표·쉼표·줄바꿈이 든 값을 처리한다.
/// </summary>
public static class Csv
{
    public static byte[] Write(IEnumerable<string> header, IEnumerable<IEnumerable<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', header.Select(Escape))).Append("\r\n");
        foreach (var row in rows)
        {
            sb.Append(string.Join(',', row.Select(v => Escape(Format(v))))).Append("\r\n");
        }
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        decimal d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        _ => value.ToString() ?? ""
    };

    private static string Escape(string value)
    {
        // 엑셀이 =, +, -, @로 시작하는 값을 수식으로 실행하지 않게 앞에 작은따옴표를 붙인다(CSV 수식 주입 방지)
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0]) && !decimal.TryParse(value, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            value = "'" + value;
        }
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    /// <summary>CSV 전체를 행 목록으로 읽는다. 첫 행은 머리글. BOM은 버린다. 끝의 빈 행은 무시한다.</summary>
    public static List<string[]> Read(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"' when field.Length == 0: quoted = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n': row.Add(field.ToString()); field.Clear(); rows.Add([.. row]); row.Clear(); break;
                default: field.Append(c); break;
            }
        }
        if (quoted) throw new FormatException("닫히지 않은 따옴표가 있습니다.");
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add([.. row]); }
        rows.RemoveAll(r => r.All(string.IsNullOrWhiteSpace));
        return rows;
    }
}
