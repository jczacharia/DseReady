// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Shared;
using Elastic.Channels;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using HealthStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus;

namespace Dse.ES;

/// <summary>Elasticsearch transport settings: cluster endpoints, authentication, TLS, and the client-side
/// distribution/resilience behavior of <see cref="DistributedTransport{T}"/>. Per-batch bulk sizing lives with each
/// source's ingestion profile, not here.</summary>
public sealed class ElasticOptions
{
    public const string SectionName = "Elastic";

    /// <summary>Cluster endpoint URL. As the only endpoint it yields a <see cref="SingleNodePool"/> — no client-side
    /// failover, so distribution is whatever sits behind it (e.g. a load-balancer VIP).</summary>
    public string BaseAddress { get; set; } = string.Empty;

    /// <summary>Additional node URLs. With more than one endpoint the client builds a <see cref="StaticNodePool"/>
    /// and owns distribution itself: per-request round-robin across <see cref="BaseAddress"/> plus these nodes,
    /// dead-node detection, and retry-on-another-node. Prefer addressing the data nodes directly over a single VIP so
    /// the transport's failover engine is actually exercised.</summary>
    public string[] Nodes { get; set; } = [];

    /// <summary>API-key authentication; takes precedence over <see cref="Username"/>/<see cref="Password"/> when set.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Basic-auth username, used only when no <see cref="ApiKey"/> is provided.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Basic-auth password paired with <see cref="Username"/>.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Hex-encoded SHA-256 fingerprint of the cluster's HTTP-layer CA certificate. The prod-safe way to
    /// trust an internally-issued certificate without installing the CA into the host trust store. Leave empty to
    /// rely on the platform certificate chain.</summary>
    public string CertificateFingerprint { get; set; } = string.Empty;

    /// <summary>Accept any server certificate. A development-only escape hatch — ignored outside Development. Use
    /// <see cref="CertificateFingerprint"/> everywhere else.</summary>
    public bool AllowUntrustedCertificates { get; set; }

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

    /// <summary>Transport-level retries for a transient or occasionally-slow node. The default covers all five production nodes.</summary>
    public int MaximumRetries { get; set; } = 4;

    /// <summary>Maximum concurrent HTTP connections per node endpoint. With a multi-node pool this is per node, so
    /// total sockets scale with the node count.</summary>
    public int ConnectionLimit { get; set; } = 80;

    /// <summary>Skip the pre-request liveness ping the transport otherwise issues to a revived or first-seen node in a
    /// multi-node pool. Pinging catches a dead node before a (large) bulk is sent at it; disabling shaves a round-trip
    /// at the cost of discovering the failure via the bulk itself. No effect on a single-node pool, which never pings.</summary>
    public bool DisablePinging { get; set; }

    /// <summary>Timeout for the liveness ping. Null leaves the transport default.</summary>
    public TimeSpan? PingTimeout { get; set; }

    /// <summary>How long a node stays benched after being marked dead before it is tried again; the backoff grows on
    /// repeated failures up to <see cref="MaxDeadTimeout"/>. Null leaves the transport default (60s). Lower it to
    /// bring a briefly-busy node back sooner; raise it to stop flapping a genuinely sick node.</summary>
    public TimeSpan? DeadTimeout { get; set; }

    /// <summary>Upper bound on the dead-node backoff. Null leaves the transport default.</summary>
    public TimeSpan? MaxDeadTimeout { get; set; }

    /// <summary>How long to wait for the channel to flush all buffered docs to Elasticsearch before failing the run.
    /// A full-corpus crawl on a large source can legitimately exceed the default.</summary>
    public TimeSpan BulkDrainTimeout { get; set; } = TimeSpan.FromHours(1);

    /// <summary><see cref="BaseAddress"/> followed by every configured node, parsed to absolute URIs — the endpoints
    /// the transport pools over. Unparseable entries are skipped (the validator rejects them up front).</summary>
    public IEnumerable<Uri> Endpoints() =>
        new[] { BaseAddress }.Concat(Nodes)
            .Select(static endpoint => Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) ? uri : null)
            .OfType<Uri>();
}

public static class ElasticExtensions
{
    public static OptionsBuilder<ElasticOptions> AddElastic(this IServiceCollection services)
    {
        services.AddSingleton<ElasticStartupService>();
        services.AddTransient<ElasticStartupData>(static sp => sp.GetRequiredService<ElasticStartupService>().Data);
        services.AddHostedService<ElasticStartupService>(static sp => sp.GetRequiredService<ElasticStartupService>());

        services.AddSingleton<DistributedTransport<IElasticsearchClientSettings>>(static sp =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            ElasticOptions opts = sp.GetRequiredService<IOptions<ElasticOptions>>().Value;
            var es = new ElasticsearchClientSettings(CreateNodePool(opts));

            if (!string.IsNullOrWhiteSpace(opts.ApiKey))
            {
                es = es.Authentication(new ApiKey(opts.ApiKey));
            }
            else if (!string.IsNullOrWhiteSpace(opts.Username) && !string.IsNullOrWhiteSpace(opts.Password))
            {
                es = es.Authentication(new BasicAuthentication(opts.Username, opts.Password));
            }

            es = es
                // Multi-node pools cap retries at the remaining known nodes; single-node pools still have no failover target.
                .MaximumRetries(opts.MaximumRetries)
                .RequestTimeout(opts.RequestTimeout)
                .MaxRetryTimeout(opts.MaxRetryTimeout)
                .ConnectionLimit(opts.ConnectionLimit);

            // Multi-node distribution/failover tuning. No-ops on a single-node pool; opt-in elsewhere so the
            // transport defaults stand unless explicitly overridden.
            if (opts.DisablePinging)
            {
                es = es.DisablePing();
            }

            if (opts.PingTimeout is { } pingTimeout)
            {
                es = es.PingTimeout(pingTimeout);
            }

            if (opts.DeadTimeout is { } deadTimeout)
            {
                es = es.DeadTimeout(deadTimeout);
            }

            if (opts.MaxDeadTimeout is { } maxDeadTimeout)
            {
                es = es.MaxDeadTimeout(maxDeadTimeout);
            }

            // Trust an internally-issued HTTP-layer certificate by its fingerprint (prod-safe).
            if (!string.IsNullOrWhiteSpace(opts.CertificateFingerprint))
            {
                es = es.CertificateFingerprint(opts.CertificateFingerprint);
            }

            // Development-only: blanket-accept server certificates. Never honored outside Development.
            if (opts.AllowUntrustedCertificates && env.IsDevelopment())
            {
                es = es.ServerCertificateValidationCallback(static (_, _, _, _) => true);
            }

            if (opts.EnableHttpCompression)
            {
                // gzip the (large) bulk ingestion payloads
                es = es.EnableHttpCompression();
            }

            return new DistributedTransport<IElasticsearchClientSettings>(env.IsDevelopment() ? es.EnableDebugMode() : es);
        });

        services.AddSingleton<ITransport>(static sp =>
            sp.GetRequiredService<DistributedTransport<IElasticsearchClientSettings>>());

        services.AddSingleton<ElasticsearchClient>(static sp =>
            new ElasticsearchClient(sp.GetRequiredService<DistributedTransport<IElasticsearchClientSettings>>()));

        services
            .AddHealthChecks()
            .AddCheck<ElasticHealthCheck>("elastic", HealthStatus.Unhealthy, ["ready"], HealthCheckDefaults.ReadinessTimeout);

        services.AddSingleton<IPostConfigureOptions<BufferOptions>, ConfigureBufferOptions>();
        services.AddSingleton<ElasticChangeTokenSource<BufferOptions>>();
        services.AddSingleton<IOptionsChangeTokenSource<BufferOptions>>(static sp =>
            sp.GetRequiredService<ElasticChangeTokenSource<BufferOptions>>());

        return services
            .AddOptions<ElasticOptions>()
            .BindConfiguration(ElasticOptions.SectionName)
            .WithFluentValidator<ElasticOptions, ElasticOptionsValidator>()
            .ValidateOnStart();
    }

    private static NodePool CreateNodePool(ElasticOptions opts)
    {
        Uri[] nodeUris = opts.Endpoints().Distinct().ToArray();
        return nodeUris.Length == 1 ? new SingleNodePool(nodeUris[0]) : new StaticNodePool(nodeUris);
    }
}

public sealed class ElasticOptionsValidator : AbstractValidator<ElasticOptions>
{
    public ElasticOptionsValidator()
    {
        RuleFor(o => o.BaseAddress)
            .NotEmpty()
            .Must(IsAbsoluteUri)
            .WithMessage("'{PropertyName}' must be an absolute URL.");

        RuleForEach(o => o.Nodes)
            .Must(IsAbsoluteUri)
            .WithMessage("Elastic node URLs must be absolute URLs.");

        RuleFor(o => o)
            .Must(static o =>
                o.Endpoints().Select(static uri => uri.Scheme).Distinct(StringComparer.OrdinalIgnoreCase).Count() <= 1)
            .WithMessage("Elastic node URLs must all use the same URI scheme.");
    }

    private static bool IsAbsoluteUri(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out _);
}
