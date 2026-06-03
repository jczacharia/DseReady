// Copyright (c) PNC Financial Services. All rights reserved.


using AwesomeAssertions;
using Dse.Shared;
using UnitsNet;

namespace Dse.Tests.Shared;

public sealed class UnitExtensionsTests
{
    [Fact]
    public void Humanize_PicksLargestWholeUnit_Decimal()
    {
        // Decimal prefixes: 5,000,000 bytes = 5 MB (not MiB).
        Information.FromBytes(5_000_000).Humanize().Should().Contain("MB");
        Information.FromBytes(2_048).Humanize().Should().Contain("KB");
    }
}
