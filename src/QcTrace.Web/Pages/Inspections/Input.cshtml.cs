using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QcTrace.Web.Data;
using QcTrace.Web.Data.Entities;
using QcTrace.Web.Services;

namespace QcTrace.Web.Pages.Inspections;

/// <summary>
/// 검사 입력. 항목별 측정값을 넣으면 규격(하한~상한)으로 자동 판정을 제안하고, 검사자가 보류로 바꿀 수 있다.
/// 판정·수량·사유의 최종 검사는 usp_ApplyInspection이 한다(R2·R4·R9).
/// </summary>
public class InputModel(QcTraceDbContext db, QcCommands qc) : QcPageModel
{
    public Lot Lot { get; private set; } = null!;
    public List<InspectionItem> Items { get; private set; } = [];

    [BindProperty] public string RequestKey { get; set; } = NewRequestKey();
    [BindProperty] public int Qty { get; set; }
    [BindProperty] public string Verdict { get; set; } = Data.Entities.Verdict.Pass;
    [BindProperty] public string? Reason { get; set; }
    /// <summary>불량 유형(불합격·보류). 목록에서 고르고 "기타"면 사유를 직접 쓴다(2026-11-01 사용자 결정)</summary>
    [BindProperty] public int? DefectCodeId { get; set; }
    public List<DefectCode> DefectCodes { get; private set; } = [];
    /// <summary>
    /// 항목 Id → 입력값. 모델 바인딩을 쓰지 않는다: Dictionary 바인딩은 Measured[..] 키가 하나도 없으면
    /// 폼 전체 키를 int로 바꾸려다 500이 난다(검사 항목이 없는 품목). OnPostAsync에서 폼을 직접 읽는다.
    /// </summary>
    public Dictionary<int, string?> Measured { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(int lotId)
    {
        if (!await LoadAsync(lotId)) return NotFound();
        Qty = Lot.Quantity!.Uninspected;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int lotId)
    {
        if (!await LoadAsync(lotId)) return NotFound();
        Measured = Items.ToDictionary(i => i.Id, i => (string?)Request.Form[$"Measured[{i.Id}]"].ToString());

        var measured = Items.Select(i => new
        {
            inspectionItemId = i.Id,
            name = i.Name,
            // 숫자가 아닌 값도 버리지 않고 그대로 보내 서버가 API와 같이 50400으로 거절하게 한다
            value = MeasuredValue(Measured.GetValueOrDefault(i.Id)),
            unit = i.Unit,
            lower = i.Lower,
            upper = i.Upper
        }).ToList();
        var measuredJson = measured.Any(m => m.value is not null) ? JsonSerializer.Serialize(measured, MeasuredJsonOptions) : null;

        // 화면에서는 불합격·보류에 불량 유형을 반드시 고르게 한다(유형별 집계용). API는 기존처럼 사유만으로도 받는다.
        if (Verdict != Data.Entities.Verdict.Pass && DefectCodeId is null && DefectCodes.Count > 0)
        {
            ModelState.AddModelError("", "불합격·보류는 불량 유형을 골라야 합니다. 목록에 없으면 \"기타\"를 고르고 사유를 쓰세요.");
            return Page();
        }
        var result = await TryRunAsync(() => qc.ApplyInspectionAsync(RequestKey, lotId, CurrentUserId, Qty, Verdict, measuredJson, Reason,
            defectCodeId: Verdict == Data.Entities.Verdict.Pass ? null : DefectCodeId));
        if (result is null) return Page();

        var q = result.Json.GetProperty("quantities");
        var overage = result.Json.GetProperty("overageQty").GetInt32();
        StatusMessage = $"{Lot.LotNo} 검사 {Qty}개 ({VerdictLabel(Verdict)}) 반영. 남은 미검사 {q.GetProperty("uninspected").GetInt32()}개"
            + (overage > 0 ? $", 생산 수량 초과분 {overage}개는 보류로 기록했습니다(R9)." : ".")
            + (result.Replayed ? " (이미 처리된 요청이라 다시 반영하지 않았습니다)" : "");
        return q.GetProperty("uninspected").GetInt32() > 0 ? RedirectToPage(new { lotId }) : RedirectToPage("./Index");
    }

    // 원장·검사 이력을 DB에서 바로 읽을 수 있게 한글을 \uXXXX로 바꾸지 않는다
    private static readonly JsonSerializerOptions MeasuredJsonOptions =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) };

    private static object? MeasuredValue(string? raw)
    {
        raw = raw?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : raw;
    }

    public static string VerdictLabel(string verdict) => verdict switch
    {
        Data.Entities.Verdict.Pass => "합격",
        Data.Entities.Verdict.Fail => "불합격",
        Data.Entities.Verdict.Hold => "보류",
        _ => verdict
    };

    private async Task<bool> LoadAsync(int lotId)
    {
        var lot = await db.Lots.AsNoTracking().Include(l => l.Item).Include(l => l.Quantity).FirstOrDefaultAsync(l => l.Id == lotId);
        if (lot is null) return false;
        Lot = lot;
        Items = await db.InspectionItems.AsNoTracking().Where(i => i.ItemId == lot.ItemId).OrderBy(i => i.SortOrder).ToListAsync();
        DefectCodes = await db.DefectCodes.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.SortOrder).ThenBy(d => d.Code).ToListAsync();
        return true;
    }
}
