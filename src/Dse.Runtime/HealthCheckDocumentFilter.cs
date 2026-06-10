// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Shared;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Dse.Runtime;

/// <summary>
///     Health endpoints are mapped with <c>MapHealthChecks</c>, which registers <c>RequestDelegate</c> endpoints that
///     carry no <see cref="System.Reflection.MethodInfo" />. The minimal-API ApiExplorer only describes endpoints that
///     expose one, so Swashbuckle never sees them. Rather than rebuild the health middleware as documented route
///     handlers (and lose its predicate/response-writer ergonomics), publish the routes here — driven off the same
///     <see cref="HealthCheckServiceOptions.Registrations" /> that <c>MapDefaultHealthChecks</c> maps, so the document
///     stays in lock-step with what's actually served.
/// </summary>
public sealed class HealthCheckDocumentFilter(IOptions<HealthCheckServiceOptions> healthChecks) : IDocumentFilter
{
    private const string Tag = "Health";

    private static OpenApiSchema PlainText => new() { Type = "string" };

    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        OpenApiSchema report = context.SchemaGenerator.GenerateSchema(typeof(DseHealthReport), context.SchemaRepository);

        // Probes: plain-text Healthy/Unhealthy, no report body.
        Add(document, "/live", "text/plain", PlainText, "Liveness probe", "Process is up — no dependency checks run.");
        Add(document, "/ready", "text/plain", PlainText, "Readiness probe",
            "Process and its ready-tagged dependencies are reachable.");

        // Aggregates: the JSON report.
        Add(document, "/health", "application/json", report, "Full health report", "Every registered check.");
        Add(document, "/health/sources", "application/json", report, "Source health report", "Checks tagged 'source'.");

        // One diagnostic route per registered check (elastic, ldap-ad, ldap-oud, source keys, …) — isolates a single
        // dependency so a flaky one can't mask another.
        foreach (string name in healthChecks.Value.Registrations
                     .Select(r => r.Name)
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            Add(document, $"/health/{name}", "application/json", report, $"Health: {name}", $"The '{name}' check in isolation.");
        }
    }

    private static void Add(
        OpenApiDocument document,
        string path,
        string contentType,
        OpenApiSchema schema,
        string summary,
        string description) =>
        document.Paths[path] = new OpenApiPathItem
        {
            Operations = new Dictionary<OperationType, OpenApiOperation>
            {
                [OperationType.Get] = new()
                {
                    Tags = [new OpenApiTag { Name = Tag }],
                    Summary = summary,
                    Description = description,
                    Responses = new OpenApiResponses
                    {
                        ["200"] = Response("Healthy or Degraded", contentType, schema),
                        ["503"] = Response("Unhealthy", contentType, schema),
                    },
                },
            },
        };

    private static OpenApiResponse Response(string description, string contentType, OpenApiSchema schema) => new()
    {
        Description = description,
        Content = { [contentType] = new OpenApiMediaType { Schema = schema } },
    };
}
