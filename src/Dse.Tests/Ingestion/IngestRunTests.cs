// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics;
using AwesomeAssertions;
using Dse.Auth;
using Dse.Data;
using Dse.Ingestion;
using Dse.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dse.Tests.Ingestion;

/// <summary>
///     End-to-end against the live Confluence + Elasticsearch stack: POST the ingest control endpoint and poll the
///     durable run (created, then driven by its <c>IngestRunCreatedEvent</c>) to a terminal phase. Entitlements are
///     injected by the test auth scheme (no LDAP).
/// </summary>
public sealed class IngestRunTests(ITestOutputHelper toh, TestFixture fixture) : TestBed(toh, fixture)
{
    private static readonly string[] s_admin = [DseEntitlements.KibanaAdminOudDn];
    private static readonly string[] s_readonly = [DseEntitlements.KibanaReadonlyOudDn];

    private SourceKey Confluence => Sources.Single(m => m.SourceKey.ToString() == "confluence").SourceKey;

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
        Guid runId = await StartAsync(key, dryRun: true, s_readonly);

        IngestRun run = await PollUntilTerminalAsync(runId, TimeSpan.FromMinutes(2));

        run.DryRun.Should().BeTrue();
        run.CurrentProgress.Checkpoint.Should().Be(IngestCheckpoint.Succeeded);
        run.Phases.Should().Contain(p => p.Checkpoint == IngestCheckpoint.Started);
        run.ActiveSourceKey.Should().BeNull("a terminal run releases its single-flight slot");
    }

    [Fact]
    public async Task Full_ingest_crawls_confluence_into_elasticsearch()
    {
        SourceKey key = Confluence;
        Guid runId = await StartAsync(key, dryRun: false, s_admin);
        IngestRun run = await PollUntilTerminalAsync(runId, TimeSpan.FromMinutes(3));

        run.CurrentProgress.Checkpoint.Should().Be(IngestCheckpoint.Succeeded);
        run.Phases.Should().Contain(p => p.Checkpoint == IngestCheckpoint.TotalMeasured);
        run.ActiveSourceKey.Should().BeNull();
    }

    [Fact]
    public async Task Second_run_while_one_is_active_is_rejected_with_409()
    {
        SourceKey key = Confluence;

        await using (AsyncServiceScope scope = Services.CreateAsyncScope())
        {
            DataContext db = scope.ServiceProvider.GetRequiredService<DataContext>();
            db.IngestRuns.Add(IngestRun.Create(key));
            await db.SaveChangesAsync(Ct);
        }

        await Http(s =>
        {
            s.Post.Url($"/sources/{key}/ingest");
            s.WithUser(s_admin);
            s.StatusCodeShouldBe(HttpStatusCode.Conflict);
        });
    }

    [Fact]
    public async Task Cancel_finalizes_the_run_and_frees_the_source()
    {
        SourceKey key = Confluence;
        Guid runId = await StartAsync(key, dryRun: false, s_admin);

        await Http(s =>
        {
            s.Post.Url($"/sources/{key}/ingest/{runId}/cancel");
            s.WithUser(s_admin);
            s.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        IngestRun run = await PollUntilTerminalAsync(runId, TimeSpan.FromMinutes(2));
        run.IsTerminal.Should().BeTrue();
        run.ActiveSourceKey.Should().BeNull();
    }

    private async Task<Guid> StartAsync(SourceKey key, bool dryRun, string[] roles)
    {
        IScenarioResult result = await Http(s =>
        {
            s.Post.Url($"/sources/{key}/ingest?dryRun={(dryRun ? "true" : "false")}");
            s.WithUser(roles);
            s.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        var accepted = await result.ReadAsJsonAsync<EntityResponse<Guid>>();
        accepted.Should().NotBeNull();
        return accepted!.Id;
    }

    private async Task<IngestRun> PollUntilTerminalAsync(Guid runId, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            await using AsyncServiceScope scope = Services.CreateAsyncScope();
            DataContext db = scope.ServiceProvider.GetRequiredService<DataContext>();
            if (await db.IngestRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, Ct) is { IsTerminal: true } run)
            {
                return run;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        }

        throw new TimeoutException($"Ingest run {runId} did not reach a terminal phase within {timeout}.");
    }
}
