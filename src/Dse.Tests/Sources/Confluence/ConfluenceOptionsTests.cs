// Copyright (c) PNC Financial Services. All rights reserved.


using AwesomeAssertions;
using Dse.Sources;
using Dse.Sources.Confluence;
using Elastic.Channels;
using Microsoft.Extensions.Options;
using Microsoft.Testing.Platform.Services;

namespace Dse.Tests.Sources.Confluence;

public sealed class ConfluenceOptionsTests(ITestOutputHelper toh, TestFixture fixture) : TestBed(toh, fixture)
{
    [Fact]
    public void ShouldBindCorrectValues()
    {
        var options = Services.GetRequiredService<IOptionsMonitor<BufferOptions>>()
            .Get(typeof(ConfluenceOptions).GetRequiredSourceKey());

        options.InboundBufferMaxSize.Should().Be(5000);
        options.OutboundBufferMaxSize.Should().Be(250);
    }
}
