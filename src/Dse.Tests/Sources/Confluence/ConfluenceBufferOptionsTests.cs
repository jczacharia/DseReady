// Copyright (c) PNC Financial Services. All rights reserved.


using AwesomeAssertions;
using Dse.ES;
using Dse.Sources;
using Dse.Sources.Confluence;
using Elastic.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dse.Tests.Sources.Confluence;

/// <summary>
///     The config-bound path from a source's <see cref="IngestionProfile" /> through to the live
///     <see cref="BufferOptions" />: the profile binds per source from the <c>Ingestion</c> section, outbound batch
///     sizing is derived from it against the probed cluster limits, and the non-derived knobs (inbound queue depth)
///     still bind from configuration.
/// </summary>
public sealed class ConfluenceBufferOptionsTests(ITestOutputHelper toh, TestFixture fixture) : TestBed(toh, fixture)
{
    private static SourceKey Key => typeof(ConfluenceOptions).GetRequiredSourceKey();

    [Fact]
    public void Profile_BindsEstimatedDocSizeFromConfiguration()
    {
        IngestionProfile profile = Services.GetRequiredService<IOptionsMonitor<IngestionProfile>>().Get(Key);

        profile.EstimatedDocBytes.Should().Be(10240);
    }

    [Fact]
    public void OutboundSizing_IsDerivedFromTheBoundProfile()
    {
        IngestionProfile profile = Services.GetRequiredService<IOptionsMonitor<IngestionProfile>>().Get(Key);
        ElasticStartupData data = Services.GetRequiredService<ElasticStartupService>().Data;
        (int expectedItems, long expectedBytes) = profile.ResolveBatch(data.BulkMaxByteSize);

        BufferOptions options = Services.GetRequiredService<IOptionsMonitor<BufferOptions>>().Get(Key);

        options.OutboundBufferMaxSize.Should().Be(expectedItems);
        options.OutboundBufferMaxBytes.Should().Be(expectedBytes);
        options.ExportMaxConcurrency.Should().Be(data.MaxChannelConcurrency);
    }

    [Fact]
    public void InboundQueueDepth_StillBindsFromConfiguration()
    {
        BufferOptions options = Services.GetRequiredService<IOptionsMonitor<BufferOptions>>().Get(Key);

        options.InboundBufferMaxSize.Should().Be(48000);
    }
}
