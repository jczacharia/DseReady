// Copyright (c) PNC Financial Services. All rights reserved.


using System.Text.Json;
using Dse.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Dse;

// Adds common .NET Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class ServiceDefaultsExtensions
{
    public static void AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        if (builder.Environment.IsDevelopment())
        {
            builder.Configuration.AddUserSecrets("dse");
        }

        builder.Services.AddDseEnvironment(builder.Configuration, builder.Environment);
        // builder.Services.AddDseOptions();
        // builder.Services.AddPingAuth();
        // builder.Services.AddLdapAuth();
        // builder.Services.AddElastic();

        builder.Services.AddProblemDetails(static s => s.ApplyCoreCustomization());
        builder.Services.ConfigureHttpClientDefaults(static o => o.RemoveAllLoggers());
        builder.Services.ConfigureHttpJsonOptions(static o => o.SerializerOptions.Converters.Add(JsonDefaults.Thinktecture));

        builder.Services.AddEndpoints();
        builder.Services.AddMemoryCache();
        builder.Services.AddServiceDiscovery();

        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Deliberately NOT AddStandardResilienceHandler() here: the only factory clients are the Confluence
            // backfill/read-through clients, which own tuned resilience pipelines — the standard handler's 30s
            // total-request-timeout would cap the backfill client's 2-minute budget. (Elasticsearch doesn't use
            // IHttpClientFactory.) Add resilience per-client as needed.
            http.AddServiceDiscovery();
        });

        if (builder is WebApplicationBuilder app)
        {
            app.WebHost.UseKestrel(static o => o.AddServerHeader = false); // Security best practice
            app.Host.UseDefaultServiceProvider(static options =>
            {
                options.ValidateScopes = true;
                options.ValidateOnBuild = true;
            });
        }
    }

    private static void ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services
            .AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter("*")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing
                    // "*" captures every ActivitySource, including the Elastic client's (it uses its own
                    // HttpClient, not the factory, so AddHttpClientInstrumentation alone would miss it).
                    .AddSource("*")
                    .AddAspNetCoreInstrumentation(t =>
                        // Exclude probe + diagnostic health traffic — high-volume, low-information, and we
                        // don't want OpenShift's probe storm dominating the trace stream. Health paths are
                        // mounted under a route-group prefix (e.g. /api) so match by suffix/contains.
                        t.Filter = context =>
                        {
                            string path = context.Request.Path.Value ?? string.Empty;
                            return !(path.EndsWith("/live", StringComparison.OrdinalIgnoreCase)
                                     || path.EndsWith("/ready", StringComparison.OrdinalIgnoreCase)
                                     || path.Contains("/health", StringComparison.OrdinalIgnoreCase));
                        })
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();
    }

    private static void AddOpenTelemetryExporters(this IHostApplicationBuilder builder)
    {
        bool useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }
    }

    private static void AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOutputCache(static t => t.AddPolicy("HealthChecks", static p => p.Expire(TimeSpan.FromSeconds(10))));

        // "self" carries the "live" tag only — `/live` is the liveness probe target and must NEVER touch a
        // dependency. A flaky ES on `/ready` should steer traffic away (readinessProbe); it must not kill the
        // pod via livenessProbe. See OpenShift readinessProbe/livenessProbe configuration in
        // dse-deploy/environments/SearchApi/rnd.yaml.
        builder.Services.AddHealthChecks().AddCheck("self", static () => HealthCheckResult.Healthy(), ["live"]);
    }

    /// <summary>
    ///     Maps the health endpoints expected by the OpenShift Helm chart (see
    ///     <c>dse-deploy/environments/SearchApi/rnd.yaml</c>). Mounted on the caller's route group — Program.cs
    ///     hangs this off <c>app.MapGroup("api")</c> alongside the business endpoints so the OpenShift Route
    ///     keeps one TLS edge + one path prefix for the whole app. Final probe paths are <c>/api/live</c> and
    ///     <c>/api/ready</c>; diagnostic paths are <c>/api/health</c>, <c>/api/health/sources</c>, and
    ///     <c>/api/health/{name}</c>.
    ///     <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 <c>/live</c> — livenessProbe + startupProbe target. Tag-filtered to <c>live</c>; the only
    ///                 contributor is the cheap "self" check, so a flaky ES/LDAP never restarts the pod.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <c>/ready</c> — readinessProbe target. Tag-filtered to <c>ready</c>: ES, LDAP, and every
    ///                 source check (Confluence, …). A failure here drops the pod out of the Service's endpoint
    ///                 list until it recovers — no restart.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <c>/health</c> — diagnostic aggregate of every registered check, rendered as JSON for
    ///                 humans. NOT a probe target.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <c>/health/sources</c> — diagnostic aggregate of every check tagged <c>source</c>.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <c>/health/{name}</c> — one route per registered check (e.g. <c>/health/elastic</c>,
    ///                 <c>/health/ldap</c>, <c>/health/confluence</c>), discovered by enumerating
    ///                 <see cref="HealthCheckServiceOptions.Registrations" />. Lets ops poke a single
    ///                 dependency without the noise of the aggregate report.
    ///             </description>
    ///         </item>
    ///     </list>
    ///     All endpoints are <c>AllowAnonymous</c> and share the <c>HealthChecks</c> 10-second output cache so
    ///     a probe storm or a curious user can't fan-out into real dependency calls.
    ///     <para>
    ///         Severity ladder driving <c>/api/ready</c>'s HTTP code via the default <c>ResultStatusCodes</c>
    ///         mapping (Healthy/Degraded → 200, Unhealthy → 503; worst-status-wins on aggregate):
    ///         <list type="bullet">
    ///             <item>
    ///                 <description>
    ///                     ES + LDAP report <c>Unhealthy</c> on failure — search and auth are non-negotiable for
    ///                     this pod to serve traffic, so a failure here MUST flip readinessProbe to 503.
    ///                 </description>
    ///             </item>
    ///             <item>
    ///                 <description>
    ///                     Source checks (Confluence, …) report <c>Degraded</c> on failure. Sources back
    ///                     ingestion and source-specific endpoints; search over the existing index keeps working
    ///                     without them, so a flaky source MUST NOT take the pod out of the Service. The
    ///                     aggregate stays at 200; the failure is still visible at <c>/api/health/{name}</c>.
    ///                 </description>
    ///             </item>
    ///         </list>
    ///     </para>
    /// </summary>
    public static RouteGroupBuilder MapDseHealthChecks(this IEndpointRouteBuilder routeBuilder)
    {
        // Mount on the caller's route group as-is. Program.cs builds `app.MapGroup("api")` and hangs both
        // endpoints and health off it, so callers get `/api/live`, `/api/ready`, `/api/health/*` automatically.
        RouteGroupBuilder healthChecks = routeBuilder.MapGroup("").AllowAnonymous();
        healthChecks.CacheOutput("HealthChecks");

        // Liveness: self only. Tag-filtered to keep dependency checks out even if they're added later.
        healthChecks.MapHealthChecks("/live", new HealthCheckOptions
        {
            Predicate = static r => r.Tags.Contains("live"),
        });

        // Readiness: every check tagged "ready" — ES, LDAP, sources. A 503 here steers traffic, doesn't restart.
        healthChecks.MapHealthChecks("/ready", new HealthCheckOptions
        {
            Predicate = static r => r.Tags.Contains("ready"),
        });

        // Diagnostic aggregate with JSON body for humans — not a probe target.
        healthChecks.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = WriteHealthReport,
        });

        healthChecks.MapHealthChecks("/health/sources", new HealthCheckOptions
        {
            Predicate = static r => r.Tags.Contains("source"),
            ResponseWriter = WriteHealthReport,
        });

        // Per-check diagnostic routes. Iterating HealthCheckServiceOptions.Registrations gives us one route per
        // registered check without per-module wiring — every IHealthCheck added via AddHealthChecks() picks this
        // up automatically.
        HealthCheckServiceOptions registry =
            routeBuilder.ServiceProvider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        foreach (string name in registry.Registrations.Select(reg => reg.Name))
        {
            healthChecks.MapHealthChecks($"/health/{name}", new HealthCheckOptions
            {
                Predicate = r => r.Name == name,
                ResponseWriter = WriteHealthReport,
            });
        }

        return healthChecks;
    }

    private static async Task WriteHealthReport(HttpContext context, HealthReport report)
    {
        string result = JsonSerializer.Serialize(
            new
            {
                status = report.Status.ToString(),
                totalDuration = report.TotalDuration.ToString(),
                checks = report.Entries.Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString(),
                    duration = e.Value.Duration.ToString(),
                    description = e.Value.Description,
                    exception = e.Value.Exception?.Message,
                    data = e.Value.Data,
                }),
            },
            JsonDefaults.Pretty);

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(result);
    }
}
