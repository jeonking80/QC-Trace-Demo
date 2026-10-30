using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace QcTrace.Web.Auth;

/// <summary>
/// 쿠키 인증의 기본 동작은 302 리다이렉트다. API는 401·403 ProblemDetails로,
/// 화면은 로그인 안 함 → 로그인 페이지, 권한 없음 → 403 상태 그대로 안내 페이지로 바꾼다.
/// </summary>
public static class AuthResponses
{
    public static Task OnRedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        if (IsApi(context.Request))
        {
            return WriteProblem(context.HttpContext, StatusCodes.Status401Unauthorized, "로그인이 필요합니다.");
        }
        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }

    public static Task OnRedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        if (IsApi(context.Request))
        {
            return WriteProblem(context.HttpContext, StatusCodes.Status403Forbidden, "이 기능에 접근할 권한이 없습니다.");
        }
        // 본문 없이 403만 남기면 UseStatusCodePagesWithReExecute가 /Status 안내 페이지를 그린다
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private static bool IsApi(HttpRequest request) =>
        request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

    private static Task WriteProblem(HttpContext http, int status, string title)
    {
        http.Response.StatusCode = status;
        return http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = new ProblemDetails { Status = status, Title = title }
        }).AsTask();
    }
}
