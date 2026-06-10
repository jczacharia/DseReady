// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using System.Security.Authentication;
using Dse.Auth;
using Dse.Data;
using Dse.ES;
using Dse.Ingestion;
using Dse.Ingestion.Events;
using Dse.Shared;
using Dse.Sources;
using Elastic.Channels;
using Humanizer;
using JasperFx;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.HealthChecks;
using Wolverine.Runtime.Serialization;
using Wolverine.Sqlite;

namespace Dse;

public static class ServiceDefaultsExtensions
{
    public static void AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        builder.AddDseOptions();
        builder.Services.AddHostedService<SourcesValidator>();

        builder.Services.AddResourceSetupOnStartup();
        builder.AddDataContext();

        builder.Services.AddWolverine(opts =>
        {
            opts.DefaultSerializer = new SystemTextJsonSerializer(JsonDefaults.Web);

            opts.ServiceName = builder.Environment.ApplicationName;
            opts.ApplicationAssembly = typeof(ServiceDefaultsExtensions).Assembly;

            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;

            opts.PersistMessagesWithSqlite(builder.Configuration.GetSqliteConnectionString());
            opts.UseEntityFrameworkCoreTransactions();
            opts.PublishDomainEventsFromEntityFrameworkCore<IAggregateRoot, IDomainEvent>(x => x.DomainEvents);

            opts.Policies.AutoApplyTransactions();
            opts.Policies.UseDurableLocalQueues();
            opts.Policies.UseDurableInboxOnAllListeners();
            opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

            opts.LocalQueueFor<IngestRunCreatedEvent>().Sequential();

            opts.Policies
                .OnException<ConcurrencyException>()
                .RetryTimes(3)
                .Then
                .MoveToErrorQueue();

            opts.Policies
                .OnException<SqliteException>()
                .Or<TimeoutException>()
                .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds())
                .Then
                .MoveToErrorQueue();

            opts.Policies
                .OnException<InvalidOperationException>()
                .Requeue()
                .AndPauseProcessing(10.Minutes());

            opts.Policies
                .OnException<AuthenticationException>()
                .MoveToErrorQueue();
        });

        builder.Services
            .AddHealthChecks()
            .AddWolverine(tags: ["live", "ready"])
            .AddWolverineListeners(tags: ["ready"]);

        builder.Services.AddSingleton<DataMigrator>();
        builder.Services.AddHostedService<DataMigrator>(static sp => sp.GetRequiredService<DataMigrator>());

        builder.Services
            .AddLdapAuth("Ad")
            .PostDseConfigure(static (options, dse) =>
            {
                if (dse.LocalCredentials() is { } cred)
                {
                    options.BindDn = options.BindDn.Or($"{cred.Username}@{options.Host}");
                    options.BindPassword = options.BindPassword.Or(cred.Password);
                }
            });

        builder.Services
            .AddLdapAuth("Oud")
            .PostDseConfigure(static (options, dse) =>
            {
                if (dse.LocalCredentials() is { } cred)
                {
                    options.BindDn = options.BindDn.Or($"cn={cred.Username},ou=Employees,ou=People,o=pnc");
                    options.BindPassword = options.BindPassword.Or(cred.Password);
                }
            });

        builder.Services
            .AddElastic()
            .PostDseConfigure(static (options, dse) =>
            {
                if (dse.LocalCredentials() is { } cred)
                {
                    options.Username = options.Username.Or(cred.Username);
                    options.Password = options.Password.Or(cred.Password);
                }
            });

        builder.Services.AddSingleton<IIngestRunControl, IngestRunControl>();

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
