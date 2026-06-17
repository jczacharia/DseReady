// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Sources;
using Elastic.Channels;
using Microsoft.Extensions.Options;

namespace Dse.ES;

// Derives each source's bulk batch dimensions from its (config-bound) IngestionProfile and the live cluster limits.
// Runs as a post-configure so it lands deterministically after config binding, and reads both the profile and
// ElasticStartupService.Data at call time so a post-probe reload picks up real cluster sizing and any config change.
internal sealed class ConfigureBufferOptions(
    ElasticStartupService startup,
    IOptionsMonitor<IngestionProfile> profiles) : IPostConfigureOptions<BufferOptions>
{
    public void PostConfigure(string? name, BufferOptions options)
    {
        ElasticStartupData data = startup.Data;
        (int items, long bytes) = profiles.Get(name ?? Options.DefaultName).ResolveBatch(data.BulkMaxByteSize);

        // Concurrency is bounded by what a single (possibly single-core) client can drive, cluster-probed.
        options.ExportMaxConcurrency = options.ExportMaxConcurrency is { } ec
            ? Math.Min(data.MaxChannelConcurrency, ec)
            : data.MaxChannelConcurrency;

        // Outbound batch sizing is derived from the source's document profile — the batch-sizing source of truth.
        // The byte budget is also a safety slice: a batch of unexpectedly large documents stays under the cluster
        // limit because the channel sub-slices the buffer to honor it.
        options.OutboundBufferMaxSize = items;
        options.OutboundBufferMaxBytes = bytes;
    }
}
