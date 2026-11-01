using System.Globalization;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;

namespace QcTrace.Web.Services;

/// <summary>
/// 기준정보(품목·검사 항목) 저장과 CSV 업로드. 기준정보는 수량·이력 테이블이 아니라 앱 계정이 직접 쓴다.
/// CSV는 전부 검사한 뒤 오류가 하나라도 있으면 아무것도 저장하지 않는다(전부 아니면 전무).
/// 키가 같으면 고치고(품목: ItemCode, 검사 항목: ItemCode+Name), 없으면 추가한다. CSV로 지우지는 않는다(사용 중지는 IsActive=0).
/// 규격(하한·상한)을 바꿔도 지난 검사 기록은 그대로다. 검사 행의 MeasuredJson에 그때 규격이 함께 남는다.
/// </summary>
public sealed class MasterData(QcTraceDbContext db)
{
    public const int MaxUploadBytes = 1024 * 1024;
    public static readonly string[] ItemHeader = ["ItemCode", "Name", "Spec", "IsActive"];
    public static readonly string[] InspectionItemHeader = ["ItemCode", "Name", "Lower", "Upper", "Unit", "SortOrder"];

    public sealed record ImportResult(int Added, int Updated, List<string> Errors)
    {
        public bool Ok => Errors.Count == 0;
    }

    // ---------- 검사 (화면 저장과 CSV가 같은 규칙을 쓴다) ----------

    public static IEnumerable<string> ValidateItem(string? code, string? name, string? spec)
    {
        if (string.IsNullOrWhiteSpace(code)) yield return "품목 코드가 필요합니다.";
        else if (code.Trim().Length > 20) yield return "품목 코드는 20자 이하입니다.";
        if (string.IsNullOrWhiteSpace(name)) yield return "품목명이 필요합니다.";
        else if (name.Trim().Length > 100) yield return "품목명은 100자 이하입니다.";
        if (spec?.Trim().Length > 200) yield return "규격 설명은 200자 이하입니다.";
    }

    public static IEnumerable<string> ValidateInspectionItem(string? name, decimal? lower, decimal? upper, string? unit, int? sortOrder)
    {
        if (string.IsNullOrWhiteSpace(name)) yield return "검사 항목명이 필요합니다.";
        else if (name.Trim().Length > 50) yield return "검사 항목명은 50자 이하입니다.";
        if (lower is null || upper is null) yield return "하한과 상한이 필요합니다.";
        else
        {
            if (lower > upper) yield return "하한이 상한보다 큽니다.";
            if (Scale(lower.Value) > 4 || Scale(upper.Value) > 4) yield return "하한·상한은 소수 4자리까지입니다.";
            if (Math.Abs(lower.Value) >= 100_000_000_000_000m || Math.Abs(upper.Value) >= 100_000_000_000_000m) yield return "하한·상한 값이 너무 큽니다.";
        }
        if (unit?.Trim().Length > 20) yield return "단위는 20자 이하입니다.";
        if (sortOrder is < 0 or > 10_000) yield return "순서는 0~10000입니다.";
    }

    private static int Scale(decimal d) => (decimal.GetBits(d)[3] >> 16) & 0xFF;

    // ---------- CSV ----------

    public async Task<ImportResult> ImportItemsAsync(string csvText)
    {
        var errors = new List<string>();
        var rows = ParseWithHeader(csvText, ItemHeader, errors);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parsed = new List<(string Code, string Name, string? Spec, bool Active)>();

        foreach (var (line, f) in rows)
        {
            var rowErrors = ValidateItem(f[0], f[1], f[2]).ToList();
            bool active = true;
            if (!string.IsNullOrWhiteSpace(f[3]) && !TryParseBool(f[3], out active)) rowErrors.Add("IsActive는 1/0 또는 true/false입니다.");
            var code = f[0].Trim();
            if (code.Length > 0 && !seen.Add(code)) rowErrors.Add($"품목 코드 {code}가 파일에 두 번 있습니다.");
            errors.AddRange(rowErrors.Select(e => $"{line}행: {e}"));
            if (rowErrors.Count == 0) parsed.Add((code, f[1].Trim(), NullIfEmpty(f[2]), active));
        }
        if (errors.Count > 0) return new ImportResult(0, 0, errors);

        var existing = await db.Items.ToDictionaryAsync(i => i.ItemCode, StringComparer.OrdinalIgnoreCase);
        int added = 0, updated = 0;
        foreach (var (code, name, spec, active) in parsed)
        {
            if (existing.TryGetValue(code, out var item))
            {
                if (item.Name != name || item.Spec != spec || item.IsActive != active)
                {
                    item.Name = name; item.Spec = spec; item.IsActive = active; updated++;
                }
            }
            else
            {
                db.Items.Add(new Item { ItemCode = code, Name = name, Spec = spec, IsActive = active });
                added++;
            }
        }
        await db.SaveChangesAsync(); // 한 번의 SaveChanges = 한 트랜잭션
        return new ImportResult(added, updated, errors);
    }

    public async Task<ImportResult> ImportInspectionItemsAsync(string csvText)
    {
        var errors = new List<string>();
        var rows = ParseWithHeader(csvText, InspectionItemHeader, errors);
        var items = await db.Items.Include(i => i.InspectionItems).ToDictionaryAsync(i => i.ItemCode, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<(string, string)>();
        var parsed = new List<(Item Item, string Name, decimal Lower, decimal Upper, string Unit, int Sort)>();

        foreach (var (line, f) in rows)
        {
            var rowErrors = new List<string>();
            var code = f[0].Trim();
            items.TryGetValue(code, out var item);
            if (code.Length == 0) rowErrors.Add("품목 코드가 필요합니다.");
            else if (item is null) rowErrors.Add($"품목 코드 {code}가 없습니다. 품목을 먼저 올리세요.");

            decimal? lower = ParseDecimal(f[2], "하한", rowErrors), upper = ParseDecimal(f[3], "상한", rowErrors);
            int? sort = 0;
            if (!string.IsNullOrWhiteSpace(f[5]))
            {
                sort = int.TryParse(f[5].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;
                if (sort is null) rowErrors.Add("순서는 정수입니다.");
            }
            if (!rowErrors.Any(e => e.StartsWith("하한") || e.StartsWith("상한")))
                rowErrors.AddRange(ValidateInspectionItem(f[1], lower, upper, f[4], sort));

            var name = f[1].Trim();
            if (code.Length > 0 && name.Length > 0 && !seen.Add((code.ToUpperInvariant(), name)))
                rowErrors.Add($"{code}의 검사 항목 {name}이 파일에 두 번 있습니다.");

            errors.AddRange(rowErrors.Select(e => $"{line}행: {e}"));
            if (rowErrors.Count == 0) parsed.Add((item!, name, lower!.Value, upper!.Value, f[4].Trim(), sort ?? 0));
        }
        if (errors.Count > 0) return new ImportResult(0, 0, errors);

        int added = 0, updated = 0;
        foreach (var (item, name, lower, upper, unit, sort) in parsed)
        {
            var existing = item.InspectionItems.FirstOrDefault(x => x.Name == name);
            if (existing is null)
            {
                item.InspectionItems.Add(new InspectionItem { Name = name, Lower = lower, Upper = upper, Unit = unit, SortOrder = sort });
                added++;
            }
            else if (existing.Lower != lower || existing.Upper != upper || existing.Unit != unit || existing.SortOrder != sort)
            {
                existing.Lower = lower; existing.Upper = upper; existing.Unit = unit; existing.SortOrder = sort;
                updated++;
            }
        }
        await db.SaveChangesAsync();
        return new ImportResult(added, updated, errors);
    }

    public async Task<byte[]> ExportItemsAsync() =>
        Csv.Write(ItemHeader, (await db.Items.AsNoTracking().OrderBy(i => i.ItemCode).ToListAsync())
            .Select(i => new object?[] { i.ItemCode, i.Name, i.Spec, i.IsActive }));

    public async Task<byte[]> ExportInspectionItemsAsync() =>
        Csv.Write(InspectionItemHeader, (await db.InspectionItems.AsNoTracking().Include(x => x.Item)
                .OrderBy(x => x.Item.ItemCode).ThenBy(x => x.SortOrder).ToListAsync())
            .Select(x => new object?[] { x.Item.ItemCode, x.Name, x.Lower, x.Upper, x.Unit, x.SortOrder }));

    /// <summary>머리글을 확인하고 (줄 번호, 열 값) 목록을 돌려준다. 열 수가 모자라면 빈 값으로 채운다.</summary>
    private static List<(int Line, string[] Fields)> ParseWithHeader(string text, string[] header, List<string> errors)
    {
        List<string[]> rows;
        try { rows = Csv.Read(text); }
        catch (FormatException ex) { errors.Add(ex.Message); return []; }

        if (rows.Count == 0) { errors.Add("빈 파일입니다."); return []; }
        var actual = rows[0].Select(h => h.Trim()).ToArray();
        if (!actual.Take(header.Length).SequenceEqual(header, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"1행 머리글은 {string.Join(",", header)} 이어야 합니다.");
            return [];
        }
        if (rows.Count == 1) { errors.Add("데이터 행이 없습니다."); return []; }

        var result = new List<(int, string[])>();
        for (var i = 1; i < rows.Count; i++)
        {
            var f = rows[i];
            if (f.Length > header.Length && f.Skip(header.Length).Any(x => !string.IsNullOrWhiteSpace(x)))
                errors.Add($"{i + 1}행: 열이 {header.Length}개보다 많습니다.");
            result.Add((i + 1, Enumerable.Range(0, header.Length).Select(c => c < f.Length ? f[c] : "").ToArray()));
        }
        return result;
    }

    private static decimal? ParseDecimal(string raw, string label, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(raw)) { errors.Add($"{label}이 필요합니다."); return null; }
        if (decimal.TryParse(raw.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return d;
        errors.Add($"{label} '{raw}'은 숫자가 아닙니다.");
        return null;
    }

    private static bool TryParseBool(string raw, out bool value)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "1": case "true": case "y": value = true; return true;
            case "0": case "false": case "n": value = false; return true;
            default: value = false; return false;
        }
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
