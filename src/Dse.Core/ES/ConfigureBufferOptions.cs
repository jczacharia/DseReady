// Copyright (c) PNC Financial Services. All rights reserved.


using Elastic.Channels;
using Microsoft.Extensions.Options;

namespace Dse.ES;

internal sealed class ConfigureBufferOptions(ElasticStartupData data) : IConfigureNamedOptions<BufferOptions>
{
    public void Configure(string? name, BufferOptions options)
    {
        options.ExportMaxConcurrency = options.ExportMaxConcurrency is { } ec
            ? Math.Min(data.MaxChannelConcurrency, ec)
            : data.MaxChannelConcurrency;

        // Safety slice, not a throughput lever: keep any single bulk request well under the cluster's
        // max_content_length. At ~10 KB/doc the nominal batch never trips this; it only guards a batch that
        // happens to carry oversized documents. Honors an explicit per-source override.
        options.OutboundBufferMaxBytes ??= Math.Min(data.BulkMaxByteSize / 4, 15L * 1024 * 1024);
    }

    public void Configure(BufferOptions options) => Configure(Options.DefaultName, options);
}
