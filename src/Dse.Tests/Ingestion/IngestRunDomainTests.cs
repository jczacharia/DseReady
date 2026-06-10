// Copyright (c) PNC Financial Services. All rights reserved.


using AwesomeAssertions;
using Dse.Data;
using Dse.Ingestion;
using Dse.Ingestion.Events;
using Dse.Sources;
using Dse.Sources.Spec;

namespace Dse.Tests.Ingestion;

/// <summary>
///     The <see cref="IngestRun" /> aggregate in isolation — pure domain rules, no host, no Elasticsearch, no
///     database. These pin the lifecycle invariants the runner and endpoints lean on: a run starts queued and
///     active, only advances forward, becomes terminal exactly once, and frees its single-flight slot when it does.
/// </summary>
public sealed class IngestRunDomainTests
{
    private static readonly SourceKey s_key = typeof(Spec).GetRequiredSourceKey();

    [Fact]
    public void Create_NewRun_StartsQueuedAndActive()
    {
        var run = IngestRun.Create(s_key, dryRun: true);

        run.CurrentProgress.Checkpoint.Should().Be(IngestCheckpoint.Queued);
        run.IsTerminal.Should().BeFalse();
        run.DryRun.Should().BeTrue();
        run.ActiveSourceKey.Should().Be(s_key);
    }

    [Fact]
    public void Create_RaisesIngestRunCreatedEventForItself()
    {
        var run = IngestRun.Create(s_key);

        ((IAggregateRoot)run).DomainEvents
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .BeOfType<IngestRunCreatedEvent>()
            .Which
            .RunId.Should()
            .Be(run.Id);
    }

    [Fact]
    public void TryClaimForExecution_QueuedRun_ReturnsTrueAndStaysQueued()
    {
        var run = IngestRun.Create(s_key);

        run.TryClaimForExecution().Should().BeTrue();
        run.CurrentProgress.Checkpoint.Should().Be(IngestCheckpoint.Queued);
        run.IsTerminal.Should().BeFalse();
    }

    [Fact]
    public void TryClaimForExecution_TerminalRun_ReturnsFalseAndDoesNotAdvance()
    {
        var run = IngestRun.Create(s_key);
        run.Advance(IngestProgress.At(IngestCheckpoint.Succeeded));

        run.TryClaimForExecution().Should().BeFalse();
        run.CurrentProgress.Checkpoint.Should().Be(IngestCheckpoint.Succeeded);
    }

    [Fact]
    public void TryClaimForExecution_AlreadyStartedRun_ReturnsFalseAndRecordsInterrupted()
    {
        // A re-delivered run that had already moved off Queued never resumes — it is finalized as Interrupted.
        var run = IngestRun.Create(s_key);
        run.Advance(IngestProgress.At(IngestCheckpoint.Started));

        run.TryClaimForExecution().Should().BeFalse();
        run.CurrentProgress.Checkpoint.Should().Be(IngestCheckpoint.Interrupted);
        run.IsTerminal.Should().BeTrue();
        run.ActiveSourceKey.Should().BeNull();
    }

    [Fact]
    public void Advance_ToTerminalCheckpoint_FreesTheSingleFlightSlot()
    {
        var run = IngestRun.Create(s_key);

        run.Advance(IngestProgress.At(IngestCheckpoint.Succeeded));

        run.IsTerminal.Should().BeTrue();
        run.ActiveSourceKey.Should().BeNull();
    }

    [Fact]
    public void Advance_AfterTerminal_IsIgnored()
    {
        var run = IngestRun.Create(s_key);
        run.Advance(IngestProgress.At(IngestCheckpoint.Canceled));

        run.Advance(IngestProgress.At(IngestCheckpoint.Started)); // late runner transition arriving after a cancel

        run.CurrentProgress.Checkpoint.Should().Be(IngestCheckpoint.Canceled);
        run.Phases.Should().NotContain(p => p.Checkpoint == IngestCheckpoint.Started);
    }

    [Fact]
    public void Cancel_RecordsCanceledTerminalAndFreesTheSlot()
    {
        var run = IngestRun.Create(s_key);
        run.Advance(IngestProgress.At(IngestCheckpoint.Started)); // mid-flight when the cancel arrives

        run.Cancel("Canceled via API.");

        run.CurrentProgress.Checkpoint.Should().Be(IngestCheckpoint.Canceled);
        run.IsTerminal.Should().BeTrue();
        run.ActiveSourceKey.Should().BeNull();
    }
}
