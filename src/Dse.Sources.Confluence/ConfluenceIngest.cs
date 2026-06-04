// Copyright (c) PNC Financial Services. All rights reserved.


using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using Dse.Ingestion;
using Elastic.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dse.Sources.Confluence;

[ExcludeFromCodeCoverage]
public sealed class ConfluenceIngest(
    IHttpClientFactory httpClientFactory,
    IOptionsSnapshot<ConfluenceOptions> options,
    ILogger<ConfluenceIngest> logger)
    : IIngest<ConfluenceDoc>
{
    private readonly string _cql = Uri.EscapeDataString(options.Value.ContentCql);
    private readonly string _expand = Uri.EscapeDataString(string.Join(separator: ',', options.Value.ContentExpand));
    private HttpClient BackFillClient => httpClientFactory.CreateClient(ConfluenceHttpClients.BackfillClient);

    public BufferOptions BufferOptions { get; } = new()
    {
        InboundBufferMaxSize = options.Value.InboundBufferMaxSize,
        OutboundBufferMaxSize = options.Value.OutboundBufferMaxSize,
    };


    public async Task<long> GetDesiredTotalToProduceAsync(CancellationToken cancellationToken)
    {
        string url = $"/rest/api/content/search?cql={_cql}&start=0&limit=0";
        ConfluenceSearchResponse response = await BackFillClient
            .GetFromJsonAsync<ConfluenceSearchResponse>(url, ConfluenceDocJsonConverter.JsonSerializerOptions, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Response content was null when deserializing.");
        return response.TotalSize;
    }

    public async Task IngestAsync(IIngestContext<ConfluenceDoc> context, CancellationToken cancellationToken)
    {
        if (context.TotalToProduce == 0)
        {
            return;
        }

        var partitions = Partitioner
            .Create(fromInclusive: 0, context.TotalToProduce, options.Value.PageSize)
            .GetDynamicPartitions()
            .Select(p => new
            {
                Start = p.Item1,
                End = p.Item2,
                Limit = p.Item2 - p.Item1,
            });

        await Parallel.ForEachAsync(partitions, new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = context.MaxConcurrency,
            },
            async (part, ct) =>
            {
                if (!await context.WaitToWriteDocAsync(ct).ConfigureAwait(false))
                {
                    logger.LogWarning("Partition {Start}-{End} skipped waiting for channel", part.Start, part.End);
                    return;
                }

                string url = $"/rest/api/content/search?cql={_cql}&start={part.Start}&limit={part.Limit}&expand={_expand}";
                ConfluenceSearchResponse page =
                    await BackFillClient
                        .GetFromJsonAsync<ConfluenceSearchResponse>(url, ConfluenceDocJsonConverter.JsonSerializerOptions, ct)
                        .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Response content was null when deserializing.");

                foreach (ConfluenceDoc doc in page.Results)
                {
                    await context.WriteDocAsync(doc, ct).ConfigureAwait(false);
                }

                if (page.Results.Count != part.Limit)
                {
                    // A small shortfall (e.g. 49 of 50) is expected — Confluence's search total is eventually
                    // consistent, and a daily re-crawl self-heals. Log it; do NOT fail the run, since aborting
                    // would discard an otherwise-complete crawl (an hour of work) over a few documents.
                    logger.LogWarning(
                        "Results count {ResultsCount} does not match expected limit {Limit} for partition {Start}-{End}",
                        page.Results.Count, part.Limit, part.Start, part.End);
                }
            });
    }
}
