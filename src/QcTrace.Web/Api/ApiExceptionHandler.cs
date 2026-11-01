using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using QcTrace.Web.Data;

namespace QcTrace.Web.Api;

/// <summary>/api 아래에서 난 예외를 ProblemDetails로 바꾼다. 화면 요청은 처리하지 않고 /Error 페이지로 넘긴다.</summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken cancellationToken)
    {
        // UseExceptionHandler("/Error")가 Request.Path를 /Error로 바꾼 뒤 호출하므로 원래 경로는 기능 객체에서 읽는다
        var path = http.Features.Get<IExceptionHandlerPathFeature>()?.Path ?? http.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && !path.Equals("/api", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var error = SqlErrors.Classify(exception);
        if (error.Status >= 500)
        {
            logger.LogError(exception, "API 오류 {Path}", path);
        }

        http.Response.StatusCode = error.Status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = error.Status,
                Title = error.Title,
                Detail = error.Detail,
                Extensions = { ["errorCode"] = error.Code }
            }
        });
    }
}
