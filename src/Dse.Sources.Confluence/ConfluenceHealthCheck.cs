// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Dse.Sources.Confluence;

/// <summary>
///     Probes Confluence's <c>/status</c> endpoint. A failure here surfaces as <see cref="HealthStatus.Degraded" />,
///     not Unhealthy — this pod can still serve search even if Confluence is down (ingestion stalls and the
///     Confluence-backed source endpoints return 5xx, but search over the existing index keeps working). The
///     default <c>ResultStatusCodes</c> mapping turns Degraded into HTTP 200 on <c>/api/ready</c>, so a flaky
///     Confluence can't drop this pod out of the OpenShift Service. The failure is still visible at
///     <c>/api/health/confluence</c> and in the <c>/api/health</c> aggregate body.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class ConfluenceHealthCheck(IHttpClientFactory factory, ILogger<ConfluenceHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpResponseMessage status = await factory.CreateClient(ConfluenceHttpClients.BackfillClient)
                .GetAsync("status", cancellationToken)
                .ConfigureAwait(false);

            return status.IsSuccessStatusCode
                ? HealthCheckResult.Healthy($"Confluence connection to {status.RequestMessage?.RequestUri?.Host} is healthy")
                : HealthCheckResult.Degraded(
                    $"Confluence connection to {status.RequestMessage?.RequestUri?.Host} "
                    + $"failed with status code {status.StatusCode} and reason {status.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Confluence connection failed: {Message}", ex.Message);
            return HealthCheckResult.Degraded($"Confluence connection failed: {ex.Message}");
        }
    }
}
