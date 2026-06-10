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
    }

    public void Configure(BufferOptions options) => Configure(Options.DefaultName, options);
}
