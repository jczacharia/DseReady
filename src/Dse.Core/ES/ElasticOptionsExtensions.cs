// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using Dse.Shared;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Microsoft.Extensions.DependencyInjection;
using HealthStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus;

namespace Dse.ES;

[ExcludeFromCodeCoverage]
public static class ElasticOptionsExtensions
{
    public static void AddElastic(this IServiceCollection services)
    {
        services
            .AddFluentOptions<ElasticOptions>(ElasticOptions.SectionName)
            .PostConfigure<IDseEnvironment>(static (o, env) =>
            {
                if (env is IDseLocalEnvironment localEnv)
                {
                    o.Username = o.Username.Or(localEnv.Username);
                    o.Password = o.Password.Or(localEnv.Password);
                }
            })
            .WithFluentValidator<ElasticOptions, ElasticOptionsValidator>();

        services.AddSingleton<ElasticStartupService>();
        services.AddTransient<ElasticStartupData>(static sp => sp.GetRequiredService<ElasticStartupService>().Data);
        services.AddHostedService<ElasticStartupService>(static sp => sp.GetRequiredService<ElasticStartupService>());

        services.AddSingleton<ElasticsearchClient>(static sp =>
        {
            var env = sp.GetRequiredService<IDseEnvironment>();
            var opts = sp.GetRequiredService<ElasticOptions>();
            var es = new ElasticsearchClientSettings(new Uri(opts.BaseAddress));

            // Decide auth from the source (the Elastic client wants typed auth, not a header): an explicit
            // ApiKey header wins, otherwise basic credentials.
            if (opts.ApiKey is { Length: > 0 } apiKey)
            {
                es = es.Authentication(new ApiKey(apiKey));
            }
            else if (opts is { Username: { Length: > 0 } u, Password: { Length: > 0 } p })
            {
                es = es.Authentication(new BasicAuthentication(u, p));
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

            return new ElasticsearchClient(env is IDseLocalEnvironment ? es.EnableDebugMode() : es);
        });

        services.AddSingleton<ITransport>(static sp => sp.GetRequiredService<ElasticsearchClient>().Transport);

        services
            .AddHealthChecks()
            .AddCheck<ElasticHealthCheck>("elastic", HealthStatus.Unhealthy, ["ready"], TimeSpan.FromSeconds(8));
    }
}
