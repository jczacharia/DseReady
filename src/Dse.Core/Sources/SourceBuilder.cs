// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Ingestion;
using Dse.Shared;
using Elastic.Ingest.Elasticsearch.Strategies;
using Elastic.Mapping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Dse.Sources;

public sealed class SourceBuilder
{
    private readonly SourceModule _module;

    public SourceBuilder(SourceModule module, IServiceCollection services)
    {
        _module = module;
        Services = services;
        Services.AddSingleton(module);
        Services.AddSingleton(module.SourceKey);
        Services.AddKeyedSingleton(module.SourceKey, module);
        Services.AddEndpoints([module.Assembly]);
        Services.AddKeyedSingleton<ElasticsearchTypeContext>(module.SourceKey, (sp, _) =>
        {
            var env = sp.GetRequiredService<DseEnv>();
            ElasticsearchTypeContext typeContext = module.GetTypeContext(env);
            return env switch
            {
                DseEnv.Test when !typeContext.ResolveAliasFormat().StartsWith("test-") => throw new InvalidOperationException(
                    $"Source module {module.GetType().Name} returned a non-test alias format in a test environment. Alias format must start with 'test-'."),
                not DseEnv.Test when !typeContext.ResolveAliasFormat().StartsWith("source-") => throw new
                    InvalidOperationException(
                        $"Source module {module.GetType().Name} returned a non-source alias format in a non-test environment. Alias format must start with 'source-'."),
                _ => typeContext,
            };
        });
    }

    public IServiceCollection Services { get; }
    public SourceKey SourceKey => _module.SourceKey;

    /// <summary>
    ///     Register the source's ingest pipeline. Adds the broadcaster once (idempotent), binds
    ///     <see cref="IIngest{TDoc}" /> to the supplied implementation, and registers a keyed
    ///     <see cref="IIngestRunner" /> the dispatcher resolves by <see cref="SourceKey" />.
    /// </summary>
    public void AddIngestion<TDoc, TIngest>()
        where TDoc : class
        where TIngest : class, IIngest<TDoc>
    {
        Services.TryAddSingleton<IngestProgressBroadcaster>();
        Services.AddScoped<IIngest<TDoc>, TIngest>();
        Services.AddKeyedScoped<IIngestRunner, IngestRunner<TDoc>>(SourceKey);
    }

    public void AddIngestStrategy<TDoc, TIngest>()
        where TDoc : class
        where TIngest : class, IIngestStrategy<TDoc> => Services.AddSingleton<IIngestStrategy<TDoc>, TIngest>();

    public void AddHealthCheck<TCheck>() where TCheck : class, IHealthCheck
    {
        string key = SourceKey.ToString();
        Services.AddHealthChecks().AddCheck<TCheck>(key, HealthStatus.Degraded, ["source", key], TimeSpan.FromSeconds(8));
    }
}
