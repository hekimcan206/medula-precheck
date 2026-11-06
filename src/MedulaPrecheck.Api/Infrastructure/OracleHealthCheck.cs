using Microsoft.Extensions.Diagnostics.HealthChecks;
using Oracle.ManagedDataAccess.Client;

namespace MedulaPrecheck.Api.Infrastructure;

/// <summary>Readiness check for the optional Oracle bulk path: can the application account open a session?</summary>
internal sealed class OracleHealthCheck(string connectionString) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new OracleConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM dual";
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (OracleException ex)
        {
            return HealthCheckResult.Unhealthy("Oracle unavailable", ex);
        }
    }
}
