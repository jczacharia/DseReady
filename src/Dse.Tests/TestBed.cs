// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Dse.Tests;

public abstract class TestBed(ITestOutputHelper toh, TestFixture fixture) : IAsyncLifetime
{
    private IAlbaHost Host => fixture.Host;
    private AsyncServiceScope TestScope { get; set; }

    protected ITestOutputHelper Out => toh;
    protected WolverineRuntime Runtime => (WolverineRuntime)Host.Services.GetRequiredService<IWolverineRuntime>();

    protected static int TimeoutMs => Debugger.IsAttached ? 1_000_000 : 5000;
    protected CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        TestScope = Host.Services.CreateAsyncScope();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await fixture.TearDownAsync(TestScope.ServiceProvider);
        await TestScope.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected T Inject<T>() where T : notnull => TestScope.ServiceProvider.GetRequiredService<T>();

    protected AlbaHostExtensions.ResponseExpression ResponseExpression(Action<Scenario> configure) => new(Host, configure);

    protected async Task<IScenarioResult> Scenario(Action<Scenario> configure)
    {
        IScenarioResult result = null!;
        await Host.TrackActivity()
            .Timeout(TimeSpan.FromMilliseconds(TimeoutMs))
            .IgnoreMessagesMatchingType(IsInfrastructureMessage)
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ => result = await Host.Scenario(configure)));
        Out.WriteLine(await result.ReadAsTextAsync());
        return result;
    }

    protected async Task<TResponse> Scenario<TResponse>(Action<Scenario> configure, Action<TResponse>? assert = null)
    {
        IScenarioResult result = await Scenario(configure);
        var response = await result.ReadAsJsonAsync<TResponse>();
        response.Should().BeAssignableTo<TResponse>();
        assert?.Invoke(response);
        return response;
    }

    protected async Task<(ITrackedSession Tracked, IScenarioResult Http)> TrackedScenario(Action<Scenario> configure)
    {
        IScenarioResult http = null!;
        ITrackedSession tracked = await Host.TrackActivity()
            .Timeout(TimeSpan.FromMilliseconds(TimeoutMs))
            .IgnoreMessagesMatchingType(IsInfrastructureMessage)
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ => http = await Host.Scenario(configure)));
        Out.WriteLine(await http.ReadAsTextAsync());
        return (tracked, http);
    }

    private static bool IsInfrastructureMessage(Type t) =>
        t.Namespace is { } ns && (ns.StartsWith("Wolverine.", StringComparison.Ordinal) ||
                                  ns.StartsWith("JasperFx.", StringComparison.Ordinal));
}
