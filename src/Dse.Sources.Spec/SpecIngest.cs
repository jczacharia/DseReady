// Copyright (c) PNC Financial Services. All rights reserved.


using System.Globalization;
using Dse.Ingestion;
using Elastic.Channels;

namespace Dse.Sources.Spec;

/// <summary>
///     Deterministic, in-process ingestion for the <see cref="Spec" /> source. It yields exactly
///     <see cref="SpecState.Total" /> synthetic documents (the runner clamps a dry run to one) straight into the
///     channel — no HTTP, no parsing, no resilience pipeline to flake. <see cref="SpecState.BeginGate" /> parks
///     production so a run can be caught mid-flight by the cancel scenario; cancellation unwinds it cooperatively.
/// </summary>
public sealed class SpecIngest(SpecState state) : IIngest<SpecDoc>
{
    public Task<long> GetDesiredTotalToProduceAsync(CancellationToken cancellationToken) =>
        Task.FromResult((long)state.Total);

    public async Task IngestAsync(IIngestContext<SpecDoc> context, CancellationToken cancellationToken)
    {
        if (context.TotalToProduce == 0)
        {
            return;
        }

        if (state.GateProduction)
        {
            state.SignalEntered();
            await state.ReleaseTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        for (long i = 0; i < context.TotalToProduce; i++)
        {
            if (!await context.WaitToWriteDocAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await context.WriteDocAsync(NewDoc(i), cancellationToken).ConfigureAwait(false);
        }
    }

    public void ConfigureBufferOptions(BufferOptions bufferOptions) => bufferOptions.OutboundBufferMaxSize = 250;

    private static SpecDoc NewDoc(long i) => new()
    {
        Id = i.ToString(CultureInfo.InvariantCulture),
        Keyword = $"keyword-{i}",
        Text = $"Document {i} body text.",
        VersionNumber = i + 1,
        Timestamp = DateTimeOffset.UtcNow,
    };
}
