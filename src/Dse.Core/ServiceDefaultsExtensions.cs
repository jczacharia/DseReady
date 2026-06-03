// Copyright (c) PNC Financial Services. All rights reserved.


using System.Text.Json;
using Dse.Data;
using Dse.ES;
using Dse.Messaging;
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

public static class ServiceDefaultsExtensions
{
    public static void AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        if (builder.Environment.IsDevelopment())
        {
            builder.Configuration.AddUserSecrets("dse");
        }

        builder.Services.AddDseEnv(builder.Configuration, builder.Environment);
        builder.Services.AddElastic();
        builder.AddData();
        builder.AddMessaging();

        builder.Services.AddProblemDetails(static s => s.ApplyCoreCustomization());
        builder.Services.ConfigureHttpClientDefaults(static o => o.RemoveAllLoggers());
        builder.Services.ConfigureHttpJsonOptions(static o => o.SerializerOptions.Converters.Add(JsonDefaults.Thinktecture));

        builder.Services.AddEndpoints([typeof(ServiceDefaultsExtensions).Assembly]);
        builder.Services.AddMemoryCache();
        builder.Services.AddServiceDiscovery();

        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddServiceDiscovery();
        });

        if (builder is WebApplicationBuilder app)
        {
            app.WebHost.UseKestrel(o => o.AddServerHeader = false); // Security best practice
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
                    .AddSource("*")
                    .AddAspNetCoreInstrumentation(t =>
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
        builder.Services.AddHealthChecks().AddCheck("self", static () => HealthCheckResult.Healthy(), ["live"]);
    }

    public static RouteGroupBuilder MapDefaultEndpoints(this IEndpointRouteBuilder routeBuilder)
    {
        RouteGroupBuilder healthChecks = routeBuilder.MapGroup("");

        healthChecks.MapHealthChecks("/live", new HealthCheckOptions
        {
            Predicate = static r => r.Tags.Contains("live"),
        });

        healthChecks.MapHealthChecks("/ready", new HealthCheckOptions
        {
            Predicate = static r => r.Tags.Contains("ready"),
        });

        healthChecks.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = WriteHealthReport,
        });

        healthChecks.MapHealthChecks("/health/sources", new HealthCheckOptions
        {
            Predicate = static r => r.Tags.Contains("source"),
            ResponseWriter = WriteHealthReport,
        });

        var healthOpts = routeBuilder.ServiceProvider.GetRequiredService<IOptions<HealthCheckServiceOptions>>();
        foreach (string name in healthOpts.Value.Registrations.Select(r => r.Name))
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
