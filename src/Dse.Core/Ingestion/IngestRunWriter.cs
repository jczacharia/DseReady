// Copyright (c) PNC Financial Services. All rights reserved.


using System.Text.Json;
using Dse.Data;
using Microsoft.EntityFrameworkCore;

namespace Dse.Ingestion;

/// <summary>
///     Appends a state-transition event for an <see cref="IngestRun" /> and updates the flat summary fields
///     on the aggregate in the same change-tracker pass. The caller is responsible for committing
///     (typically <c>SaveChangesAndFlushMessagesAsync</c> via the Wolverine outbox).
/// </summary>
public static class IngestRunWriter
{
    /// <summary>
    ///     Append <paramref name="payload" /> to the event log for <paramref name="run" /> and apply its
    ///     side effects to the flat <see cref="IngestRun" /> summary.
    /// </summary>
    /// <remarks>
    ///     <see cref="IngestRunEvent.Seq" /> is computed as <c>MAX(Seq) + 1</c> over existing rows for the run.
    ///     Per-run sequencing is correct under SQLite's single-writer model and under any future provider as
    ///     long as concurrent transitions for the same run are serialized — today that's a single endpoint
    ///     request; once a real progress handler chain exists, partition the Wolverine local queue by
    ///     <see cref="Entity.Id" /> so events for one run land on a single worker.
    /// </remarks>
    public static async Task<IngestRunEvent> AppendAsync(
        this DataContext db,
        IngestRun run,
        IngestEventPayload payload,
        CancellationToken ct = default)
    {
        long nextSeq = (await db.IngestRunEvents
            .Where(e => e.RunId == run.Id)
            .MaxAsync(e => (long?)e.Seq, ct) ?? 0L) + 1L;

        var evt = new IngestRunEvent
        {
            RunId = run.Id,
            Seq = nextSeq,
            At = DateTimeOffset.UtcNow,
            Type = payload.GetType().Name,
            Payload = JsonSerializer.Serialize<IngestEventPayload>(payload, IngestEventPayloadJson.Options),
        };

        db.IngestRunEvents.Add(evt);
        run.Apply(payload, evt.At);
        return evt;
    }

    /// <summary>Project a payload onto the flat <see cref="IngestRun" /> summary.</summary>
    private static void Apply(this IngestRun run, IngestEventPayload payload, DateTimeOffset at)
    {
        switch (payload)
        {
            case IngestEventPayload.Queued:
                run.Phase = IngestPhase.Queued;
                break;

            case IngestEventPayload.Started:
                run.Phase = IngestPhase.Started;
                run.StartedAt ??= at;
                break;

            case IngestEventPayload.Bootstrapped b:
                run.Phase = IngestPhase.Bootstrapped;
                run.TargetIndex = b.IndexName;
                break;

            case IngestEventPayload.TotalMeasured t:
                run.Phase = IngestPhase.TotalMeasured;
                run.TotalItems = t.Total;
                break;

            case IngestEventPayload.Ingesting i:
                run.Phase = IngestPhase.Ingesting;
                run.ItemsIngested = i.Snapshot.Produced;
                break;

            case IngestEventPayload.Draining d:
                run.Phase = IngestPhase.Draining;
                run.ItemsIngested = d.Snapshot.Produced;
                break;

            case IngestEventPayload.Aliasing a:
                run.Phase = IngestPhase.Aliasing;
                run.ItemsIngested = a.Snapshot.Produced;
                break;

            case IngestEventPayload.Succeeded s:
                run.Phase = IngestPhase.Succeeded;
                run.ItemsIngested = s.Snapshot.Produced;
                run.EndedAt ??= at;
                break;

            case IngestEventPayload.Failed f:
                run.Phase = IngestPhase.Failed;
                run.FailureReason = f.Reason;
                run.EndedAt ??= at;
                break;

            case IngestEventPayload.Faulted f:
                run.Phase = IngestPhase.Faulted;
                run.FailureReason = f.Exception.Message;
                run.EndedAt ??= at;
                break;

            case IngestEventPayload.Canceled c:
                run.Phase = IngestPhase.Canceled;
                run.FailureReason = c.Reason;
                run.EndedAt ??= at;
                break;

            default:
                throw new InvalidOperationException($"Unhandled IngestEventPayload: {payload.GetType().FullName}");
        }
    }
}
