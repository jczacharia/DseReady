// Copyright (c) PNC Financial Services. All rights reserved.


using AwesomeAssertions;
using Dse.Sources;

namespace Dse.Tests.Sources;

/// <summary>
///     <see cref="IngestionProfile.ResolveBatch" /> in isolation — the pure sizing math that turns a source's
///     document profile plus the cluster's max bulk content length into concrete batch dimensions. No host, no
///     Elasticsearch. These pin the contract the buffer post-configure leans on: bytes track the fraction and stay
///     within the cluster limit, and item count tracks document size while honoring the declared clamps.
/// </summary>
public sealed class IngestionProfileTests
{
    private const long HundredMb = 100L * 1024 * 1024;

    [Fact]
    public void Default_SizesItemsToLandNearTheByteBudget()
    {
        var profile = new IngestionProfile();

        (int items, long bytes) = profile.ResolveBatch(HundredMb);

        // 25% of 100 MiB, then that budget / 16 KiB per doc.
        bytes.Should().Be(HundredMb / 4);
        items.Should().Be((int)(bytes / profile.EstimatedDocBytes));
    }

    [Fact]
    public void SmallerDocuments_ProduceLargerBatches()
    {
        var small = new IngestionProfile { EstimatedDocBytes = 1_024 };
        var large = new IngestionProfile { EstimatedDocBytes = 1_024 * 1_024 };

        small.ResolveBatch(HundredMb).Items.Should().BeGreaterThan(large.ResolveBatch(HundredMb).Items);
    }

    [Fact]
    public void TinyDocuments_ClampItemsToMax()
    {
        var profile = new IngestionProfile { EstimatedDocBytes = 1, MaxBatchItems = 5_000 };

        profile.ResolveBatch(HundredMb).Items.Should().Be(5_000);
    }

    [Fact]
    public void HugeDocuments_ClampItemsToMin()
    {
        var profile = new IngestionProfile { EstimatedDocBytes = HundredMb, MinBatchItems = 8 };

        // The byte budget holds far fewer than one whole document, but the floor still applies.
        profile.ResolveBatch(HundredMb).Items.Should().Be(8);
    }

    [Theory]
    [InlineData(0.25, HundredMb / 4)]
    [InlineData(0.5, HundredMb / 2)]
    [InlineData(1.0, HundredMb)]
    public void ByteBudget_TracksTheFraction(double fraction, long expectedBytes)
    {
        var profile = new IngestionProfile { BatchByteFraction = fraction };

        profile.ResolveBatch(HundredMb).Bytes.Should().Be(expectedBytes);
    }

    [Fact]
    public void ByteBudget_StaysPositiveForATinyCluster()
    {
        var profile = new IngestionProfile();

        (int items, long bytes) = profile.ResolveBatch(clusterMaxContentBytes: 1);

        bytes.Should().Be(1);
        items.Should().Be(profile.MinBatchItems);
    }
}
