// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using Dse.Data;
using Dse.ES;
using Dse.Ingestion;
using Dse.Messaging;
using Dse.Shared;
using Dse.Sources;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
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

        builder.Services.AddDseEnvironment();
        builder.Services.AddHostedService<SourcesValidator>();

        builder.Services.AddResourceSetupOnStartup();
        builder.AddDataContext();
        builder.AddMessaging();

        builder.Services.AddSingleton<DataMigrator>();
        builder.Services.AddHostedService<DataMigrator>(static sp => sp.GetRequiredService<DataMigrator>());

        builder.Services.AddElastic();
        builder.AddIngestion();

        builder.Services.AddProblemDetails(static s => s.ApplyCoreCustomization());
        builder.Services.ConfigureHttpClientDefaults(static o => o.RemoveAllLoggers());
        builder.Services.ConfigureHttpJsonOptions(static o => o.SerializerOptions.Converters.Add(JsonDefaults.Thinktecture));

        builder.Services.AddMemoryCache();
        builder.Services.AddServiceDiscovery();

        builder.RemoveWindowsEventLogProvider();
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

    // The generic host auto-adds a Windows Event Log provider whenever it runs on Windows. This service is a Linux
    // container that logs to the console and OpenTelemetry on every platform — it has no Event Log to write to.
    // Worse, the runtime project pins a linux-x64 RuntimeIdentifier, so on a dev Windows box the provider binds to
    // the non-Windows System.Diagnostics.EventLog stub and throws PlatformNotSupportedException as the host eagerly
    // builds its loggers. Drop it so startup is identical on every OS. (Provider construction is eager — a log-level
    // filter can't prevent it; the descriptor has to go.)
    private static void RemoveWindowsEventLogProvider(this IHostApplicationBuilder builder)
    {
        const string eventLogProvider = "Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider";

        foreach (ServiceDescriptor descriptor in builder.Services
                     .Where(d => d.ServiceType == typeof(ILoggerProvider)
                                 && d.ImplementationType?.FullName == eventLogProvider)
                     .ToList())
        {
            builder.Services.Remove(descriptor);
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

    private static void AddDefaultHealthChecks(this IHostApplicationBuilder builder) =>
        builder.Services.AddHealthChecks().AddCheck("self", static () => HealthCheckResult.Healthy(), ["live"]);

    public static RouteGroupBuilder MapDefaultHealthChecks(
        this IEndpointRouteBuilder routeBuilder,
        [StringSyntax("Route")] string pattern = "")
    {
        RouteGroupBuilder healthChecks = routeBuilder.MapGroup(pattern);

        healthChecks.MapHealthChecks("live", new HealthCheckOptions
        {
            Predicate = static r => r.Tags.Contains("live"),
        });

        healthChecks.MapHealthChecks("ready", new HealthCheckOptions
        {
            Predicate = static r => r.Tags.Contains("ready"),
        });

        healthChecks.MapHealthChecks("health", new HealthCheckOptions().WithReportWriter());

        healthChecks.MapHealthChecks("health/sources", new HealthCheckOptions
        {
            Predicate = static r => r.Tags.Contains("source"),
        }.WithReportWriter());

        var healthOpts = routeBuilder.ServiceProvider.GetRequiredService<IOptions<HealthCheckServiceOptions>>();
        foreach (string name in healthOpts.Value.Registrations.Select(r => r.Name))
        {
            healthChecks.MapHealthChecks($"health/{name}", new HealthCheckOptions
            {
                Predicate = r => r.Name == name,
            }.WithReportWriter());
        }

        return healthChecks;
    }
}
