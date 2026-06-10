// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Ingestion;
using Dse.Shared;
using Elastic.Channels;
using Elastic.Mapping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Dse.Sources;

public sealed class SourceBuilder<TDoc> where TDoc : class
{
    private readonly SourceModule _module;

    public SourceBuilder(SourceModule module, IServiceCollection services)
    {
        _module = module;
        Services = services;
        Services.AddSingleton(module);
        Services.AddSingleton(module.SourceKey);
        Services.AddKeyedSingleton(module.SourceKey, module);
        Services.AddOptions<BufferOptions>(SourceKey).BindConfiguration(SourceKey);
        Services.AddKeyedSingleton<ElasticsearchTypeContext>(module.SourceKey, (sp, _) =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            ElasticsearchTypeContext typeContext = module.GetTypeContext(env);
            string aliasFormat = typeContext.ResolveAliasFormat();

            if (env.IsTest() && !aliasFormat.StartsWith("test-"))
            {
                throw new InvalidOperationException(
                    $"Source module {module.GetType().Name} returned a non-test alias format in a test environment."
                    + $" Alias format must start with 'test-'.");
            }

            if (!env.IsTest() && !aliasFormat.StartsWith("source-"))
            {
                throw new InvalidOperationException(
                    $"Source module {module.GetType().Name} returned a non-source alias format in a non-test environment."
                    + $" Alias format must start with 'source-'.");
            }

            return typeContext;
        });
    }

    public IServiceCollection Services { get; }
    public SourceKey SourceKey => _module.SourceKey;

    public OptionsBuilder<TOptions> AddOptions<TOptions>() where TOptions : class =>
        Services.AddOptions<TOptions>().BindConfiguration(SourceKey).ValidateDataAnnotations().ValidateOnStart();

    public void AddIngestion<TIngest>() where TIngest : class, IIngest<TDoc>
    {
        Services.AddScoped<IIngest<TDoc>, TIngest>();
        Services.AddKeyedScoped<IIngestRunner, IngestRunner<TDoc>>(SourceKey);
    }

    public void AddHealthCheck<TCheck>(HealthStatus status = HealthStatus.Degraded) where TCheck : class, IHealthCheck =>
        Services.AddHealthChecks().AddCheck<TCheck>(SourceKey, status, ["source", SourceKey], TimeSpan.FromSeconds(8));
}
