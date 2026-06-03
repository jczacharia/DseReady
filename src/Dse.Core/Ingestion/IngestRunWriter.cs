// Copyright (c) PNC Financial Services. All rights reserved.


using System.Text.Json;
using Dse.Data;
using Microsoft.EntityFrameworkCore;

namespace Dse.Ingestion;

/// <summary>Appends an event and projects it onto the flat <see cref="IngestRun" /> summary.</summary>
public static class IngestRunWriter
{
    /// <remarks>
    ///     Seq is MAX+1 per run. Concurrent writers for the same run must be serialized — partition the
    ///     Wolverine queue by RunId once a real handler chain exists.
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
