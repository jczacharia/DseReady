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

public sealed class ElasticOptions
{
    public const string SectionName = "Elastic";

    [Url]
    [Required]
    public string BaseAddress { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public double NodeUtilization { get; set; } = 0.75d;
    public double ClientOversubscription { get; set; } = 2.0d;
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
                // gzip the (large) bulk ingestion payloads
                .EnableHttpCompression()
                // Resilience for a single, occasionally-slow node.
                // A single static node defaults to MaximumRetries=0 and MaxRetryTimeout==RequestTimeout.
                // E.g.: One slow call fails with zero retries.
                .MaximumRetries(3)
                .RequestTimeout(TimeSpan.FromSeconds(60))
                .MaxRetryTimeout(TimeSpan.FromSeconds(180));

            return new ElasticsearchClient(env.IsDevelopment() ? es.EnableDebugMode() : es);
        });

        services.AddSingleton<ITransport>(static sp => sp.GetRequiredService<ElasticsearchClient>().Transport);

        services
            .AddHealthChecks()
            .AddCheck<ElasticHealthCheck>("elastic", HealthStatus.Unhealthy, ["ready"], TimeSpan.FromSeconds(8));

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
