// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics;
using AwesomeAssertions;
using Dse.Sources;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
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
    protected WolverineRuntime Runtime => (WolverineRuntime)Host.Services.GetRequiredService<IWolverineRuntime>();

    protected static int TimeoutMs => Debugger.IsAttached ? 1_000_000 : 5000;
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        TestScope = Host.Services.CreateAsyncScope();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await fixture.TearDownAsync(Services);
        await (TestScope?.DisposeAsync() ?? ValueTask.CompletedTask);
        GC.SuppressFinalize(this);
    }

    protected AlbaHostExtensions.ResponseExpression ResponseExpression(Action<Scenario> configure) => new(Host, configure);

    // Plain HTTP call with no Wolverine activity tracking — for async endpoints (e.g. 202-then-background) where
    // we must NOT wait for the published message to finish before the response returns.
    protected Task<IScenarioResult> Http(Action<Scenario> configure) => Host.Scenario(configure);

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

    protected async Task<ProblemDetails> Problem(Action<Scenario> configure, Action<ProblemDetails>? assert = null)
    {
        IScenarioResult result = await Scenario(configure);
        var response = await result.ReadAsJsonAsync<ProblemDetails>();
        response.Should().BeAssignableTo<ProblemDetails>();
        assert?.Invoke(response);
        return response;
    }

    // Like Scenario, but with a caller-chosen timeout — long-running durable work (e.g. an ingestion driven by a
    // domain event) needs far more than the default budget, and the tracked session is what guarantees the
    // background handler has finished before the test returns (so teardown never races a live run).
    protected async Task<IScenarioResult> TrackedHttp(Action<Scenario> configure, TimeSpan timeout)
    {
        IScenarioResult result = null!;
        await Host.TrackActivity()
            .Timeout(timeout)
            .IgnoreMessagesMatchingType(IsInfrastructureMessage)
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ => result = await Host.Scenario(configure)));
        Out.WriteLine(await result.ReadAsTextAsync());
        return result;
    }

    protected async Task<(ITrackedSession Tracked, IScenarioResult Http)> TrackedScenario(
        Action<Scenario> configure,
        Action<ITrackedSession, IScenarioResult>? assert = null)
    {
        IScenarioResult http = null!;
        ITrackedSession tracked = await Host.TrackActivity()
            .Timeout(TimeSpan.FromMilliseconds(TimeoutMs))
            .IgnoreMessagesMatchingType(IsInfrastructureMessage)
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ => http = await Host.Scenario(configure)));
        Out.WriteLine(await http.ReadAsTextAsync());
        assert?.Invoke(tracked, http);
        return (tracked, http);
    }

    private static bool IsInfrastructureMessage(Type t) =>
        t.Namespace is { } ns && (ns.StartsWith("Wolverine.", StringComparison.Ordinal) ||
                                  ns.StartsWith("JasperFx.", StringComparison.Ordinal));

    protected Task ForEachSourceAsync(Func<SourceModule, Task> task) => Assert.MultipleAsync(
        Sources.Select(module => (Func<Task>)(() => task(module))).ToArray());
}
