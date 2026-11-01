using Microsoft.Data.SqlClient;

namespace QcTrace.Web.Data;

/// <summary>
/// 저장 프로시저의 THROW 번호(Data/Sql/README.md)와 연결 장애를 HTTP 응답으로 분류한다.
/// </summary>
public static class SqlErrors
{
    public sealed record Error(int Status, string Title, string? Detail, string Code);

    // 연결 자체가 안 되는 경우: 타임아웃, 서버 없음(이름 해석 실패 포함), 연결 끊김, DB 열기 실패, 로그인 실패
    private static readonly HashSet<int> Unavailable =
        [-2, -1, 2, 26, 35, 40, 53, 121, 233, 1225, 4060, 10053, 10054, 10060, 10061, 11001, 18456, 40613];

    public static Error Classify(Exception exception)
    {
        var sql = Find<SqlException>(exception);
        if (sql is null)
        {
            return new Error(StatusCodes.Status500InternalServerError, "서버 오류가 발생했습니다.", null, "server_error");
        }

        return sql.Number switch
        {
            50400 => new Error(StatusCodes.Status400BadRequest, "입력값이 올바르지 않습니다.", sql.Message, "invalid_input"),
            50403 => new Error(StatusCodes.Status403Forbidden, "이 작업에 필요한 역할이 없습니다.", sql.Message, "role_required"),
            50404 => new Error(StatusCodes.Status404NotFound, "대상을 찾을 수 없습니다.", sql.Message, "not_found"),
            50409 => new Error(StatusCodes.Status409Conflict, "요청 키 충돌", sql.Message, "R6"),
            52001 => new Error(StatusCodes.Status422UnprocessableEntity, "업무 규칙 위반", sql.Message, "R1"),
            52004 => new Error(StatusCodes.Status422UnprocessableEntity, "업무 규칙 위반", sql.Message, "R4"),
            52005 => new Error(StatusCodes.Status422UnprocessableEntity, "업무 규칙 위반", sql.Message, "R5"),
            52007 => new Error(StatusCodes.Status422UnprocessableEntity, "업무 규칙 위반", sql.Message, "R7"),
            52011 => new Error(StatusCodes.Status422UnprocessableEntity, "업무 규칙 위반", sql.Message, "R3"),
            // Class 20 이상은 연결이 끊긴 심각한 오류다
            _ when Unavailable.Contains(sql.Number) || sql.Class >= 20 =>
                new Error(StatusCodes.Status503ServiceUnavailable, "데이터베이스에 연결할 수 없습니다.", "잠시 뒤 다시 시도하세요.", "db_unavailable"),
            _ => new Error(StatusCodes.Status500InternalServerError, "서버 오류가 발생했습니다.", null, "server_error")
        };
    }

    private static T? Find<T>(Exception? exception) where T : Exception
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is T match) return match;
        }
        return null;
    }
}
