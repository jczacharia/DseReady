// Copyright (c) PNC Financial Services. All rights reserved.


using System.ComponentModel.DataAnnotations;
using Elastic.Channels;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using HealthStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus;

namespace Dse.ES;

/// <summary>Elasticsearch client settings: cluster endpoint, authentication, and bulk-ingest throughput/resilience tuning.</summary>
public sealed class ElasticOptions
{
    public const string SectionName = "Elastic";

    /// <summary>Cluster endpoint URL.</summary>
    [Url]
    [Required]
    public string BaseAddress { get; set; } = string.Empty;

    /// <summary>API-key authentication; takes precedence over <see cref="Username"/>/<see cref="Password"/> when set.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Basic-auth username, used only when no <see cref="ApiKey"/> is provided.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Basic-auth password paired with <see cref="Username"/>.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Fraction of the cluster write thread-pool capacity this client may drive concurrently.</summary>
    public double NodeUtilization { get; set; } = 0.75d;

    /// <summary>Absolute cap on concurrent bulk exports. For an I/O-bound ingest the binding limit is the client
    /// itself, not core count; clamped down to the cluster write-pool ceiling at startup.</summary>
    public int MaxExportConcurrency { get; set; } = 30;

    /// <summary>Gzip bulk payloads — trades CPU for network. Disable when Elasticsearch is network-local and the
    /// client is CPU-bound (e.g. a single-core pod) so those cycles go to crawl/serialize instead.</summary>
    public bool EnableHttpCompression { get; set; } = true;

    /// <summary>Per-request timeout. A large bulk to a busy cluster can exceed the default under load.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Overall retry budget across attempts for a single call (a static node otherwise caps this at the request timeout).</summary>
    public TimeSpan MaxRetryTimeout { get; set; } = TimeSpan.FromSeconds(180);

    /// <summary>Transport-level retries for a transient or occasionally-slow node.</summary>
    public int MaximumRetries { get; set; } = 3;

    /// <summary>How long to wait for the channel to flush all buffered docs to Elasticsearch before failing the run.
    /// A full-corpus crawl on a large source can legitimately exceed the default.</summary>
    public TimeSpan BulkDrainTimeout { get; set; } = TimeSpan.FromHours(1);
}

public static class ElasticExtensions
{
    public static OptionsBuilder<ElasticOptions> AddElastic(this IServiceCollection services)
    {
        services.AddSingleton<ElasticStartupService>();
        services.AddTransient<ElasticStartupData>(static sp => sp.GetRequiredService<ElasticStartupService>().Data);
        services.AddHostedService<ElasticStartupService>(static sp => sp.GetRequiredService<ElasticStartupService>());

        services.AddSingleton<ElasticsearchClient>(static sp =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            ElasticOptions opts = sp.GetRequiredService<IOptions<ElasticOptions>>().Value;
            var es = new ElasticsearchClientSettings(new Uri(opts.BaseAddress));

            if (!string.IsNullOrWhiteSpace(opts.ApiKey))
            {
                es = es.Authentication(new ApiKey(opts.ApiKey));
            }
            else if (!string.IsNullOrWhiteSpace(opts.Username) && !string.IsNullOrWhiteSpace(opts.Password))
            {
                es = es.Authentication(new BasicAuthentication(opts.Username, opts.Password));
            }

            es = es
                // Resilience for a single, occasionally-slow node.
                // A single static node defaults to MaximumRetries=0 and MaxRetryTimeout==RequestTimeout.
                // E.g.: One slow call fails with zero retries.
                .MaximumRetries(opts.MaximumRetries)
                .RequestTimeout(opts.RequestTimeout)
                .MaxRetryTimeout(opts.MaxRetryTimeout);

            if (opts.EnableHttpCompression)
            {
                // gzip the (large) bulk ingestion payloads
                es = es.EnableHttpCompression();
            }

            return new ElasticsearchClient(env.IsDevelopment() ? es.EnableDebugMode() : es);
        });

        services.AddSingleton<ITransport>(static sp => sp.GetRequiredService<ElasticsearchClient>().Transport);

        services
            .AddHealthChecks()
            .AddCheck<ElasticHealthCheck>("elastic", HealthStatus.Unhealthy, ["ready"], HealthCheckDefaults.ReadinessTimeout);

        services.AddSingleton<IConfigureOptions<BufferOptions>, ConfigureBufferOptions>();
        services.AddSingleton<ElasticChangeTokenSource<BufferOptions>>();
        services.AddSingleton<IOptionsChangeTokenSource<BufferOptions>>(sp =>
            sp.GetRequiredService<ElasticChangeTokenSource<BufferOptions>>());

        return services
            .AddOptions<ElasticOptions>()
            .BindConfiguration(ElasticOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }
}
