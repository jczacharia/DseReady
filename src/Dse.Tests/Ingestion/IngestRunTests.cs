// Copyright (c) PNC Financial Services. All rights reserved.


using AwesomeAssertions;
using Dse.Auth;
using Dse.Data;
using Dse.Ingestion;
using Dse.Ingestion.Endpoints;
using Dse.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dse.Tests.Ingestion;

/// <summary>
///     End-to-end against a real Elasticsearch with Confluence stubbed at the HTTP seam. The POST returns 202 and
///     the ingestion runs durably in the background off an <c>IngestRunCreatedEvent</c>; a Wolverine tracked
///     session is what tells us that background handler has finished — no polling, and teardown never races a
///     live run. Entitlements are injected by the test auth scheme (no LDAP).
/// </summary>
public sealed class IngestRunTests(ITestOutputHelper toh, TestFixture fixture) : TestBed(toh, fixture)
{
    private static readonly string[] s_admin = [DseEntitlements.KibanaAdminOudDn];
    private static readonly string[] s_readonly = [DseEntitlements.KibanaReadonlyOudDn];
    private static readonly TimeSpan s_ingestTimeout = TimeSpan.FromSeconds(15);

    private SourceKey Confluence => Sources.Single(m => m.SourceKey.ToString() == "confluence").SourceKey;
    private StubConfluenceState Stub => Services.GetRequiredService<StubConfluenceState>();

    [Fact]
    public async Task Full_ingest_requires_the_admin_entitlement()
    {
        SourceKey key = Confluence;

        await Http(s =>
        {
            s.Post.Url($"/sources/{key}/ingest");
            s.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });

        await Http(s =>
        {
            s.Post.Url($"/sources/{key}/ingest");
            s.WithUser(s_readonly);
            s.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }

    [Fact]
    public async Task Dry_ingest_runs_a_single_document_and_succeeds()
    {
        SourceKey key = Confluence;
        Stub.Total = 5; // a dry run clamps its own work to one document regardless of corpus size.
        Guid runId = await StartAndAwaitAsync(key, dryRun: true, s_readonly);

        IngestRun run = await ReadRunAsync(runId);
        run.DryRun.Should().BeTrue();
        run.CurrentProgress.Checkpoint.Should().Be(IngestCheckpoint.Succeeded);
        run.Phases.Should().Contain(p => p.Checkpoint == IngestCheckpoint.Started);
        run.ActiveSourceKey.Should().BeNull("a terminal run releases its single-flight slot");

        // The status endpoint must serialize cleanly — the phase→run back-reference must not cycle.
        IScenarioResult statusResult = await Http(s =>
        {
            s.Get.Url($"/sources/{key}/ingest/{runId}");
            s.WithUser(s_readonly);
            s.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var status = await statusResult.ReadAsJsonAsync<IngestRunStatus>();
        status.Should().NotBeNull();
        status!.Id.Should().Be(runId);
        status.Phases.Should().Contain(p => p.Checkpoint == IngestCheckpoint.Succeeded);
    }

    [Fact]
    public async Task Overview_reports_idle_then_history_after_a_run()
    {
        SourceKey key = Confluence;

        // Idle to begin with: a source is listed, nothing in flight.
        IngestionOverview before = await OverviewAsync();
        SourceIngestionStatus confluenceBefore = before.Sources.Single(s => s.SourceKey == key);
        confluenceBefore.State.Should().Be(IngestState.Idle);
        confluenceBefore.Current.Should().BeNull();

        Stub.Total = 5;
        Guid runId = await StartAndAwaitAsync(key, dryRun: true, s_admin);

        // After a completed run: back to idle, with the run recorded in history.
        IngestionOverview after = await OverviewAsync();
        SourceIngestionStatus confluenceAfter = after.Sources.Single(s => s.SourceKey == key);
        confluenceAfter.State.Should().Be(IngestState.Idle);
        confluenceAfter.History.Should().Contain(r => r.RunId == runId && r.Checkpoint == IngestCheckpoint.Succeeded);
    }

    [Fact]
    public async Task Second_run_while_one_is_active_is_rejected_with_409()
    {
        SourceKey key = Confluence;
        await SeedActiveRunAsync(key);

        await Http(s =>
        {
            s.Post.Url($"/sources/{key}/ingest");
            s.WithUser(s_admin);
            s.StatusCodeShouldBe(HttpStatusCode.Conflict);
        });
    }

    [Fact]
    public async Task Cancel_finalizes_an_in_flight_run_and_frees_the_source()
    {
        SourceKey key = Confluence;
        Stub.Total = 5;
        Stub.BeginGate(); // park the document-fetch so the run is provably mid-ingest when we cancel.

        // A dry run still fetches its one clamped document, so it parks on the same gate. Start the durable
        // ingestion but don't await it yet — the tracked session settles only once the background handler has
        // unwound, which is exactly what we want to wait on AFTER cancelling.
        Task<IScenarioResult> ingest = TrackedHttp(s =>
        {
            s.Post.Url($"/sources/{key}/ingest?dryRun=true");
            s.WithUser(s_admin);
            s.StatusCodeShouldBe(HttpStatusCode.Accepted);
        }, s_ingestTimeout);

        await Stub.PageFetchEntered.WaitAsync(Ct); // runner is now blocked inside the ingest phase.
        Guid runId = await ActiveRunIdAsync(key);

        await Http(s =>
        {
            s.Post.Url($"/sources/{key}/ingest/{runId}/cancel");
            s.WithUser(s_admin);
            s.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        Stub.Release();   // belt-and-suspenders; cancellation already trips the parked fetch.
        await ingest;     // the handler defers to the recorded terminal and completes; teardown is now safe.

        IngestRun run = await ReadRunAsync(runId);
        run.CurrentProgress.Checkpoint.Should().Be(IngestCheckpoint.Canceled);
        run.IsTerminal.Should().BeTrue();
        run.ActiveSourceKey.Should().BeNull();
    }

    private async Task<Guid> StartAndAwaitAsync(SourceKey key, bool dryRun, string[] roles)
    {
        IScenarioResult result = await TrackedHttp(s =>
        {
            s.Post.Url($"/sources/{key}/ingest?dryRun={(dryRun ? "true" : "false")}");
            s.WithUser(roles);
            s.StatusCodeShouldBe(HttpStatusCode.Accepted);
        }, s_ingestTimeout);

        var accepted = await result.ReadAsJsonAsync<EntityResponse<Guid>>();
        accepted.Should().NotBeNull();
        return accepted!.Id;
    }

    private async Task SeedActiveRunAsync(SourceKey key)
    {
        // A plain save does not flush domain events, so no background runner is started — the run simply holds
        // the source's single-flight slot, which is all these control-plane assertions need.
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        DataContext db = scope.ServiceProvider.GetRequiredService<DataContext>();
        db.IngestRuns.Add(IngestRun.Create(key));
        await db.SaveChangesAsync(Ct);
    }

    private async Task<Guid> ActiveRunIdAsync(SourceKey key)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        DataContext db = scope.ServiceProvider.GetRequiredService<DataContext>();
        return await db.IngestRuns
            .Where(r => r.SourceKey == key && r.ActiveSourceKey != null)
            .Select(r => r.Id)
            .SingleAsync(Ct);
    }

    private async Task<IngestRun> ReadRunAsync(Guid runId)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        DataContext db = scope.ServiceProvider.GetRequiredService<DataContext>();
        return await db.IngestRuns.AsNoTracking().FirstAsync(r => r.Id == runId, Ct);
    }

    private async Task<IngestionOverview> OverviewAsync()
    {
        IScenarioResult result = await Http(s =>
        {
            s.Get.Url("/ingestion");
            s.WithUser(s_readonly);
            s.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var overview = await result.ReadAsJsonAsync<IngestionOverview>();
        overview.Should().NotBeNull();
        return overview!;
    }
}
