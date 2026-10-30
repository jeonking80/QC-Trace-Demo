using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace QcTrace.Web.Pages;

/// <summary>본문 없는 오류 응답(403, 404 등)의 안내 페이지. 상태 코드는 원래 값을 유지한다.</summary>
[IgnoreAntiforgeryToken]
public class StatusModel : PageModel
{
    public int Code { get; private set; }
    public string Title { get; private set; } = "";
    public string Message { get; private set; } = "";

    public void OnGet(int code) => Set(code);

    public void OnPost(int code) => Set(code);

    private void Set(int code)
    {
        Code = code;
        (Title, Message) = code switch
        {
            403 => ("접근 권한이 없습니다", "이 화면은 담당 역할이 있는 사용자만 볼 수 있습니다. 역할이 필요하면 관리자에게 요청하세요."),
            404 => ("페이지를 찾을 수 없습니다", "주소를 다시 확인하세요."),
            _ => ("요청을 처리하지 못했습니다", "")
        };
    }
}
