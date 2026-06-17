// Copyright (c) PNC Financial Services. All rights reserved.


using System.Collections.Concurrent;
using System.Net.Http.Json;
using Dse.Ingestion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dse.Sources.Confluence;

public sealed class ConfluenceIngest(
    IHttpClientFactory httpClientFactory,
    IOptionsSnapshot<ConfluenceOptions> options,
    ILogger<ConfluenceIngest> logger) : IIngest<ConfluenceDoc>
{
    private readonly string _cql = Uri.EscapeDataString(options.Value.ContentCql);
    private readonly string _expand = Uri.EscapeDataString(string.Join(separator: ',', options.Value.ContentExpand));
    private HttpClient BackFillClient => httpClientFactory.CreateClient(ConfluenceHttpClients.BackfillClient);

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
                MaxDegreeOfParallelism = options.Value.CrawlConcurrency,
            },
            async (part, ct) =>
            {
                // Back-pressure gate — this MUST be the first thing in the partition body, before the
                // Confluence request below. WaitToWriteDocAsync blocks while the export channel is full
                // (i.e. Elasticsearch is behind). Gating the fetch here means we stop pulling pages from
                // Confluence when we can't write them, so fetched ConfluenceDocs can't pile up unbounded
                // in memory. Checking AFTER the request would defeat this: the page would already be
                // downloaded and sitting in memory before we ever discover the channel was full.
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
