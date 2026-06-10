// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Sources;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Dse.Tests;

public abstract class TestBed(ITestOutputHelper toh, TestFixture fixture) : IAsyncLifetime
{
    private IAlbaHost Host => fixture.Host;
    private AsyncServiceScope? TestScope { get; set; }
    protected IServiceProvider Services => TestScope.HasValue ? TestScope.Value.ServiceProvider : Host.Services;
    protected SourceModule[] Sources => Host.Services.GetServices<SourceModule>().ToArray();

    protected ITestOutputHelper Out => toh;
    protected WolverineRuntime Runtime => Host.GetRuntime();

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        TestScope = Host.Services.CreateAsyncScope();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await fixture.ResetAsync();
        await (TestScope?.DisposeAsync() ?? ValueTask.CompletedTask);
        GC.SuppressFinalize(this);
    }

    protected async Task<IScenarioResult> Scenario(Action<Scenario> configure)
    {
        IScenarioResult result = await Host.Scenario(configure);
        Out.WriteLine(await result.ReadAsTextAsync());
        return result;
    }

    protected async Task<(ITrackedSession Tracked, IScenarioResult Result)> TrackedScenario(Action<Scenario> configure)
    {
        IScenarioResult result = null!;
        ITrackedSession tracked = await Host.ExecuteAndWaitAsync(async Task (_) => result = await Host.Scenario(configure));
        Out.WriteLine(await result.ReadAsTextAsync());
        return (tracked, result);
    }

    protected Task ForEachSourceAsync(Func<SourceModule, Task> task) => Assert.MultipleAsync(
        Sources.Select(module => (Func<Task>)(() => task(module))).ToArray());
}
