using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace QcTrace.Web.Health;

/// <summary>
/// ConnectionStrings:QcTrace로 SQL Server에 접속해 SELECT 1을 실행한다.
/// </summary>
public sealed class SqlServerHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString("QcTrace");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return HealthCheckResult.Unhealthy("ConnectionStrings:QcTrace 설정이 없습니다.");
        }

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy($"SQL Server 연결 성공 ({connection.ServerVersion})");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("SQL Server 연결 실패", ex);
        }
    }
}
