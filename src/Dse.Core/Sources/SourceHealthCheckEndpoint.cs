// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using Dse.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace Dse.Sources;

public static class SourceHealthCheckEndpoint
{
    public static IEndpointConventionBuilder MapSourceHealthCheck(
        this SourcePipelineBuilder builder,
        [StringSyntax("Route")] string pattern,
        Action<HealthCheckOptions>? configure = null)
    {
        HealthCheckOptions options = new() { Predicate = r => r.Name == builder.SourceKey.ToString() };
        options.WithReportWriter();
        configure?.Invoke(options);
        return builder.MapHealthChecks(pattern, options);
    }
}
