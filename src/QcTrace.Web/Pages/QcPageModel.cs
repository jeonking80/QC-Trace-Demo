using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using QcTrace.Web.Data;
using QcTrace.Web.Services;

namespace QcTrace.Web.Pages;

/// <summary>
/// 업무 화면 공통. SP가 거절한 요청(업무 규칙 위반 등)은 화면에 메시지로 보여주고, 서버 장애는 그대로 올린다.
/// 화면 검증은 편의일 뿐이고, 규칙 판단은 항상 SP가 한다.
/// </summary>
public abstract class QcPageModel : PageModel
{
    [TempData]
    public string? StatusMessage { get; set; }

    protected string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>폼마다 새 요청 키. 같은 폼을 두 번 제출(새로고침·더블클릭)해도 SP가 한 번만 반영한다(R6).</summary>
    public static string NewRequestKey() => Guid.NewGuid().ToString("N");

    protected async Task<CommandResult?> TryRunAsync(Func<Task<CommandResult>> command)
    {
        try
        {
            return await command();
        }
        catch (SqlException ex)
        {
            var error = SqlErrors.Classify(ex);
            if (error.Status >= 500) throw;
            ModelState.AddModelError("", error.Detail ?? error.Title);
            return null;
        }
    }
}
