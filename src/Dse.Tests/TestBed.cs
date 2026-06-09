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
    protected IAlbaHost Host => fixture.Host;
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

    protected async Task<(ITrackedSession Tracked, IScenarioResult Result)> Scenario(
        Action<Scenario> configure,
        Action<ITrackedSession, IScenarioResult>? assert = null)
    {
        IScenarioResult result = null!;
        ITrackedSession tracked = await Host.TrackActivity()
            .Timeout(TimeSpan.FromSeconds(10))
            .ExecuteAndWaitAsync(async Task (_) => result = await Host.Scenario(configure));
        Out.WriteLine(await result.ReadAsTextAsync());
        assert?.Invoke(tracked, result);
        return (tracked, result);
    }

    protected async Task<ProblemDetails> Problem(
        Action<Scenario> configure,
        Action<ITrackedSession, ProblemDetails>? assert = null)
    {
        (ITrackedSession tracked, IScenarioResult result) = await Scenario(configure);
        var response = await result.ReadAsJsonAsync<ProblemDetails>();
        result.Context.Response.StatusCode.Should().BeGreaterThanOrEqualTo(400);
        response.Should().BeAssignableTo<ProblemDetails>();
        assert?.Invoke(tracked, response);
        return response;
    }
}
