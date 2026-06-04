// Copyright (c) PNC Financial Services. All rights reserved.


using AwesomeAssertions;
using Dse.Sources;
using Dse.Sources.Confluence;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dse.Tests.Sources;

public sealed class SourcesValidatorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SourcesValidator Validator(params SourceModule[] modules) =>
        new(modules, NullLogger<SourcesValidator>.Instance);

    [Fact]
    public async Task StartAsync_WithOneModule_Succeeds()
    {
        SourcesValidator validator = Validator(new Dse.Sources.Confluence.Confluence());
        Func<Task> fn = async () =>
        {
            await validator.StartAsync(Ct); // completes without throwing
            await validator.StopAsync(Ct);
        };
        await fn.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_WithNoModules_Throws()
    {
        Func<Task> act = () => Validator().StartAsync(Ct);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("No source modules");
    }

    [Fact]
    public async Task StartAsync_WithDuplicateKeys_Throws()
    {
        // Two Confluence modules share the "confluence" key — a misconfiguration the validator must catch.
        Func<Task> act = () => Validator(new Dse.Sources.Confluence.Confluence(), new Dse.Sources.Confluence.Confluence()).StartAsync(Ct);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Duplicate source keys");
    }
}
