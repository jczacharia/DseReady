// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Novell.Directory.Ldap;

namespace Dse.Auth;

/// <summary>
///     Liveness of one LDAP directory: connect (and bind, if credentials are configured) through its
///     <see cref="LdapConnector" />. Directory-agnostic — it labels itself from the connector, so the same check
///     serves every keyed connector (AD, OUD, …); one registration per key supplies the right one.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class LdapHealthCheck(LdapConnector connector, ILogger<LdapHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        LdapAuthOptions options = connector.Options;
        string target = $"{connector.Name} ({options.Host}:{options.Port})";

        try
        {
            LdapConnection conn = await connector.GetConnectionAsync(cancellationToken).ConfigureAwait(false);

            // Reachable and authenticated is healthy; reachable but unbound means no credentials were configured
            // (anonymous) — the directory is up, so degrade rather than fail the readiness gate. A bad credential
            // throws from BindAsync and lands in the catch below.
            return conn.Bound
                ? HealthCheckResult.Healthy($"{target} bound")
                : HealthCheckResult.Degraded($"{target} connected but not bound");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LDAP health check failed for {Target}", target);
            return HealthCheckResult.Unhealthy($"{target} failed: {ex.Message}", ex);
        }
    }
}
