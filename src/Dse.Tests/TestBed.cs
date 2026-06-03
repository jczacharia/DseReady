// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics;
using AwesomeAssertions;
using Elastic.Clients.Elasticsearch;
using Elastic.Mapping;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Dse.Tests;

public abstract class TestBed(ITestOutputHelper toh, TestFixture fixture) : IAsyncLifetime
{
    private IAlbaHost Host => fixture.Host;
    private IServiceProvider Services => fixture.Host.Services;
    private AsyncServiceScope TestScope { get; set; }
    protected ITestOutputHelper TestOutputHelper => toh;
    protected ElasticsearchClient EsClient => Services.GetRequiredService<ElasticsearchClient>();
    protected WolverineRuntime Runtime => (WolverineRuntime)Services.GetRequiredService<IWolverineRuntime>();

    protected static int TimeoutMs => Debugger.IsAttached ? 1_000_000 : 5000;
    protected CancellationToken Ct => TestContext.Current.CancellationToken;

    protected T Inject<T>() where T : notnull => TestScope.ServiceProvider.GetRequiredService<T>();

    public async ValueTask InitializeAsync()
    {
        await Host.ClearAllPersistedWolverineDataAsync(Ct);
        TestScope = Services.CreateAsyncScope();
    }

    public async ValueTask DisposeAsync()
    {
        var esClient = Services.GetRequiredService<ElasticsearchClient>();
        foreach (var typeContext in Services.GetServices<ElasticsearchTypeContext>())
        {
            string index = typeContext.ResolveIndexFormat(); // test-{sourceKey}-<guid>
            Assert.StartsWith("test-", index);
            await esClient.Indices.DeleteAsync(index, Ct);
            await esClient.Indices.DeleteIndexTemplateAsync($"{index}-template", Ct);
            await esClient.Cluster.DeleteComponentTemplateAsync($"{index}-template-mappings", Ct);
            await esClient.Cluster.DeleteComponentTemplateAsync($"{index}-template-settings", Ct);
        }

        await TestScope.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected AlbaHostExtensions.ResponseExpression ResponseExpression(Action<Scenario> configure) => new(Host, configure);

    protected async Task<IScenarioResult> Scenario(Action<Scenario> configure)
    {
        IScenarioResult result = null!;
        await Host.ExecuteAndWaitAsync(async () => result = await Host.Scenario(configure), TimeoutMs);
        TestOutputHelper.WriteLine(await result.ReadAsTextAsync());
        return result;
    }

    protected async Task<TResponse> Scenario<TResponse>(Action<Scenario> configure, Action<TResponse>? responseFn = null)
    {
        IScenarioResult result = null!;
        await Host.ExecuteAndWaitAsync(async () => result = await Host.Scenario(configure), TimeoutMs);
        TestOutputHelper.WriteLine(await result.ReadAsTextAsync());
        TResponse response = await result.ReadAsJsonAsync<TResponse>();
        response.Should().BeAssignableTo<TResponse>();
        responseFn?.Invoke(response);
        return response;
    }
}
