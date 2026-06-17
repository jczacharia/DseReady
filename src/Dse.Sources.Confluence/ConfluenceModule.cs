// Copyright (c) PNC Financial Services. All rights reserved.


using System.ComponentModel.DataAnnotations;
using Dse.Ingestion.Endpoints;
using Dse.Shared;
using Dse.Sources;
using Dse.Sources.Confluence;
using Elastic.Mapping;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Wolverine.Attributes;

[assembly: SourceManifest<Confluence>]
[assembly: WolverineModule]

namespace Dse.Sources.Confluence;

/// <summary>Confluence source connection, crawl concurrency, HTTP resilience, and the CQL/expansions that define what gets ingested.</summary>
public sealed class ConfluenceOptions
{
    /// <summary>Confluence base URL.</summary>
    public string BaseAddress { get; set; } = string.Empty;

    /// <summary>Service-account username; falls back to local-dev credentials when unset.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Password for <see cref="Username"/>.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Optional outbound HTTP proxy.</summary>
    public string? Proxy { get; set; }

    /// <summary>Results per search request. Confluence caps the body-expanded search at 50 — a hard server limit.</summary>
    [Range(minimum: 1, maximum: 50)]
    public int PageSize { get; set; } = 50;

    /// <summary>Concurrent search requests during a crawl. Throughput scales ~linearly
    /// (docs/sec ≈ CrawlConcurrency × PageSize / pageLatency) until the network, server, or client core saturates.
    /// Independent of ES export concurrency.</summary>
    [Range(minimum: 1, maximum: 256)]
    public int CrawlConcurrency { get; set; } = 64;

    /// <summary>Socket-pool ceiling. <see langword="null"/> tracks <see cref="CrawlConcurrency"/> so the pool never
    /// throttles the crawl; set it to cap sockets below the crawl width (proxy or per-client connection limit).</summary>
    [Range(minimum: 1, maximum: 256)]
    public int? MaxConnectionsPerServer { get; set; }

    /// <summary>Backfill (unattended crawl) resilience: wraps all retry attempts for one request.</summary>
    public TimeSpan BackfillTotalTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Backfill per-attempt timeout bounding a single try.</summary>
    public TimeSpan BackfillAttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum backfill retry attempts after the first failure.</summary>
    [Range(minimum: 0, maximum: 20)]
    public int BackfillMaxRetryAttempts { get; set; } = 5;

    /// <summary>Initial backoff delay between backfill retries.</summary>
    public TimeSpan BackfillRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Cap on the (backing-off) backfill retry delay.</summary>
    public TimeSpan BackfillRetryMaxDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Interactive read-through (body-view/asset) budget: one tight timeout, no retries — fail fast for the browser.</summary>
    public TimeSpan ReadThroughTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Pooled socket lifetime for DNS-rotation correctness; rarely tuned per environment.</summary>
    public TimeSpan PooledConnectionLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Idle timeout before a pooled socket is reclaimed.</summary>
    public TimeSpan PooledConnectionIdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>TCP connect timeout.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>CQL selecting and ordering the content to crawl.</summary>
    public string ContentCql { get; set; } = "type in (page,blogpost) order by lastModified desc";

    /// <summary>Confluence expansions fetched per result to populate the indexed document.</summary>
    public string[] ContentExpand { get; set; } =
    [
        "ancestors",
        "body.storage",
        "history",
        "metadata.labels",
        "space",
        "version",
    ];
}

public sealed class Confluence() : SourceModule<ConfluenceDoc>("confluence")
{
    public override ElasticsearchTypeContext GetTypeContext(IHostEnvironment env) => env.IsTest()
        ? ConfluenceContext.ConfluenceDocTest.CreateContext(Guid.NewGuid().ToString())
        : ConfluenceContext.ConfluenceDoc.Context with { IndexPatternUseBatchDate = true };

    public override void Register(SourceBuilder<ConfluenceDoc> builder)
    {
        builder
            .AddOptions<ConfluenceOptions>()
            .PostDseConfigure(static (o, dse) =>
            {
                if (dse.LocalCredentials() is { } cred)
                {
                    o.Username = o.Username.Or(cred.Username);
                    o.Password = o.Password.Or(cred.Password);
                }
            });

        builder.Services.AddConfluenceHttpClients();
        builder.AddHealthCheck<ConfluenceHealthCheck>();
        builder.AddIngestion<ConfluenceIngest>();
    }

    public override void Configure(SourcePipelineBuilder builder)
    {
        builder.MapIngestEndpoint();
        builder.MapDryIngestEndpoint();
        builder.MapGetIngestRunEndpoint();
        builder.MapCancelIngestRunEndpoint();

        builder
            .MapSearchEndpoint()
            .RequireAuthorization(p => p.RequireConfluenceEntitlement());

        builder
            .MapConfluenceBodyViewEndpoint()
            .RequireAuthorization(p => p.RequireConfluenceEntitlement());
    }
}
