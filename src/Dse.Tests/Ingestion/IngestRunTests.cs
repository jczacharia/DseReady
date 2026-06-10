// Copyright (c) PNC Financial Services. All rights reserved.


using AwesomeAssertions;
using Dse.Auth;
using Dse.Data;
using Dse.Ingestion;
using Dse.Ingestion.Endpoints;
using Dse.Sources;
using Dse.Sources.Spec;
using Elastic.Clients.Elasticsearch;
using Elastic.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dse.Tests.Ingestion;

/// <summary>
///     End-to-end exercise of the ingestion engine against a real Elasticsearch, driven by the in-process
///     <c>Spec</c> source — no HTTP seam to stub. The POST returns 202 and ingestion runs durably in the background
///     off an <c>IngestRunCreatedEvent</c>; a Wolverine tracked session is what tells us the background handler has
///     finished — no polling, and teardown never races a live run. Entitlements come from the test auth scheme.
/// </summary>
public sealed class IngestRunTests(ITestOutputHelper toh, TestFixture fixture) : TestBed(toh, fixture)
{
    private static readonly string[] s_admin = [DseEntitlements.KibanaAdminOudDn];
    private static readonly string[] s_readonly = [DseEntitlements.KibanaReadonlyOudDn];

    private static SourceModule SpecModule => typeof(Spec).GetRequiredSourceModule();
    private SpecState State => Services.GetRequiredService<SpecState>();

    [Fact]
    public Task Full_ingest_requires_the_admin_entitlement() => ForEachSourceAsync(async module =>
    {
        await Scenario(s =>
        {
            s.Post.Url($"/sources/{module.SourceKey}/ingest");
            s.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });

        await Scenario(s =>
        {
            s.Post.Url($"/sources/{module.SourceKey}/ingest");
            s.WithUser(s_readonly);
            s.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    });

    [Fact]
    public Task Dry_ingest_requires_the_authenticated_user() => ForEachSourceAsync(module => Scenario(s =>
    {
        s.Post.Url($"/sources/{module.SourceKey}/ingest/dry");
        s.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
    }));

    [Fact]
    public async Task Dry_ingest_runs_a_single_document_and_succeeds()
    {
        State.Total = 5; // a dry run clamps its own work to one document regardless of corpus size.
        Guid runId = await StartAndAwaitAsync(SpecModule.SourceKey, dryRun: true, s_readonly);

        IngestRun run = await ReadRunAsync(runId);
        run.DryRun.Should().BeTrue();
        run.CurrentProgress.Checkpoint.Should().Be(IngestCheckpoint.Succeeded);
        run.Phases.Should().Contain(p => p.Checkpoint == IngestCheckpoint.Started);
        run.ActiveSourceKey.Should().BeNull("a terminal run releases its single-flight slot");

        // The status endpoint must serialize cleanly — the phase→run back-reference must not cycle.
        IScenarioResult result = await Scenario(s =>
        {
            s.Get.Url($"/sources/{SpecModule.SourceKey}/ingest/{runId}");
            s.WithUser(s_readonly);
            s.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var status = await result.ReadAsJsonAsync<IngestRunStatus>();
        status.Should().NotBeNull();
        status!.Id.Should().Be(runId);
        status.Phases.Should().Contain(p => p.Checkpoint == IngestCheckpoint.Succeeded);
    }

    [Fact]
    public async Task Overview_reports_idle_then_history_after_a_run()
    {
        // Idle to begin with: a source is listed, nothing in flight.
        IngestionOverview before = await OverviewAsync();
        SourceIngestionStatus specBefore = before.Sources.Single(s => s.SourceKey == SpecModule.SourceKey);
        specBefore.State.Should().Be(IngestState.Idle);
        specBefore.Current.Should().BeNull();

        State.Total = 5;
        Guid runId = await StartAndAwaitAsync(SpecModule.SourceKey, dryRun: true, s_admin);

        // After a completed run: back to idle, with the run recorded in history.
        IngestionOverview after = await OverviewAsync();
        SourceIngestionStatus specAfter = after.Sources.Single(s => s.SourceKey == SpecModule.SourceKey);
        specAfter.State.Should().Be(IngestState.Idle);
        specAfter.History.Should().Contain(r => r.RunId == runId && r.Checkpoint == IngestCheckpoint.Succeeded);
    }

    [Fact]
    public async Task Second_run_while_one_is_active_is_rejected_with_409()
    {
        await SeedActiveRunAsync(SpecModule.SourceKey);

        await Scenario(s =>
        {
            s.Post.Url($"/sources/{SpecModule.SourceKey}/ingest");
            s.WithUser(s_admin);
            s.StatusCodeShouldBe(HttpStatusCode.Conflict);
        });
    }

    [Fact]
    public async Task Cancel_finalizes_an_in_flight_run_and_frees_the_source()
    {
        State.Total = 5;
        State.BeginGate(); // park production so the run is provably mid-ingest when we cancel.

        // A dry run still produces its one clamped document, so it parks on the same gate. Start the durable
        // ingestion but don't await it yet — the tracked session settles only once the background handler has
        // unwound, which is exactly what we want to wait on AFTER cancelling.
        Task<IScenarioResult> ingest = Scenario(s =>
        {
            s.Post.Url($"/sources/{SpecModule.SourceKey}/ingest/dry");
            s.WithUser(s_admin);
            s.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        await State.Entered.WaitAsync(Ct); // runner is now blocked inside the ingest phase.
        Guid runId = await ActiveRunIdAsync(SpecModule.SourceKey);

        await Scenario(s =>
        {
            s.Post.Url($"/sources/{SpecModule.SourceKey}/ingest/{runId}/cancel");
            s.WithUser(s_admin);
            s.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        State.Release(); // belt-and-suspenders; cancellation already trips the parked production.
        await ingest; // the handler defers to the recorded terminal and completes; teardown is now safe.

        IngestRun run = await ReadRunAsync(runId);
        run.CurrentProgress.Checkpoint.Should().Be(IngestCheckpoint.Canceled);
        run.IsTerminal.Should().BeTrue();
        run.ActiveSourceKey.Should().BeNull();
    }

    [Fact]
    public async Task Full_ingest_indexes_every_document_and_is_searchable()
    {
        State.Total = 7;
        Guid runId = await StartAndAwaitAsync(SpecModule.SourceKey, dryRun: false, s_admin);

        IngestRun run = await ReadRunAsync(runId);
        run.DryRun.Should().BeFalse();
        run.CurrentProgress.Checkpoint.Should().Be(IngestCheckpoint.Succeeded);
        run.Phases.Should()
            .Contain(p => p.Checkpoint == IngestCheckpoint.Aliasing,
                "a full run promotes the alias — the dry path never reaches Aliasing");

        (await SearchableCountAsync(SpecModule.SourceKey)).Should()
            .Be(expected: 7, "every produced document is indexed and searchable after aliasing");
    }

    [Fact]
    public async Task Empty_corpus_run_succeeds_and_indexes_nothing()
    {
        State.Total = 0;
        Guid runId = await StartAndAwaitAsync(SpecModule.SourceKey, dryRun: false, s_admin);

        IngestRun run = await ReadRunAsync(runId);
        run.CurrentProgress.Checkpoint.Should().Be(IngestCheckpoint.Succeeded);
        (await SearchableCountAsync(SpecModule.SourceKey)).Should().Be(0);
    }

    [Fact]
    public Task Status_for_an_unknown_run_returns_404() => Scenario(s =>
    {
        s.Get.Url($"/sources/{SpecModule.SourceKey}/ingest/{Guid.NewGuid()}");
        s.WithUser(s_readonly);
        s.StatusCodeShouldBe(HttpStatusCode.NotFound);
    });

    [Fact]
    public Task Cancel_for_an_unknown_run_returns_404() => Scenario(s =>
    {
        s.Post.Url($"/sources/{SpecModule.SourceKey}/ingest/{Guid.NewGuid()}/cancel");
        s.WithUser(s_admin);
        s.StatusCodeShouldBe(HttpStatusCode.NotFound);
    });

    [Fact]
    public async Task Cancel_an_already_finished_run_returns_409()
    {
        State.Total = 1;
        Guid runId = await StartAndAwaitAsync(SpecModule.SourceKey, dryRun: true,
            s_admin); // runs through to a terminal checkpoint

        await Scenario(s =>
        {
            s.Post.Url($"/sources/{SpecModule.SourceKey}/ingest/{runId}/cancel");
            s.WithUser(s_admin);
            s.StatusCodeShouldBe(HttpStatusCode.Conflict);
        });
    }

    [Fact]
    public async Task Overview_reports_running_while_a_run_is_in_flight()
    {
        State.Total = 5;
        State.BeginGate(); // park production so the run is provably mid-ingest while we observe it.

        Task<IScenarioResult> ingest = Scenario(s =>
        {
            s.Post.Url($"/sources/{SpecModule.SourceKey}/ingest/dry");
            s.WithUser(s_admin);
            s.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        await State.Entered.WaitAsync(Ct); // runner is now blocked inside the ingest phase.

        IngestionOverview overview = await OverviewAsync();
        SourceIngestionStatus spec = overview.Sources.Single(s => s.SourceKey == SpecModule.SourceKey);
        spec.State.Should().Be(IngestState.Running);
        spec.Current.Should().NotBeNull();
        spec.Current!.IsTerminal.Should().BeFalse();

        State.Release();
        await ingest; // let the run settle so teardown never races a live run.
    }

    private async Task<long> SearchableCountAsync(SourceKey key)
    {
        var typeContext = Services.GetRequiredKeyedService<ElasticsearchTypeContext>(key);
        var elastic = Services.GetRequiredService<ElasticsearchClient>();
        CountResponse response = await elastic.CountAsync(new CountRequest(typeContext.ResolveReadTarget()), Ct);
        return response.Count;
    }

    private async Task<Guid> StartAndAwaitAsync(SourceKey key, bool dryRun, string[] roles)
    {
        (_, IScenarioResult result) = await TrackedScenario(s =>
        {
            s.Post.Url($"/sources/{key}/ingest{(dryRun ? "/dry" : string.Empty)}");
            s.WithUser(roles);
            s.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        var accepted = await result.ReadAsJsonAsync<EntityResponse<Guid>>();
        accepted.Should().NotBeNull();
        return accepted!.Id;
    }

    private async Task SeedActiveRunAsync(SourceKey key)
    {
        // A plain save does not flush domain events, so no background runner is started — the run simply holds
        // the source's single-flight slot, which is all these control-plane assertions need.
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataContext>();
        db.IngestRuns.Add(IngestRun.Create(key));
        await db.SaveChangesAsync(Ct);
    }

    private async Task<Guid> ActiveRunIdAsync(SourceKey key)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataContext>();
        return await db.IngestRuns
            .Where(r => r.SourceKey == key && r.ActiveSourceKey != null)
            .Select(r => r.Id)
            .SingleAsync(Ct);
    }

    private async Task<IngestRun> ReadRunAsync(Guid runId)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataContext>();
        return await db.IngestRuns.AsNoTracking().FirstAsync(r => r.Id == runId, Ct);
    }

    private async Task<IngestionOverview> OverviewAsync()
    {
        IScenarioResult result = await Scenario(s =>
        {
            s.Get.Url("/ingestion");
            s.WithUser(s_readonly);
            s.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var overview = await result.ReadAsJsonAsync<IngestionOverview>();
        overview.Should().NotBeNull();
        return overview;
    }
}
