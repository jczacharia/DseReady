// Copyright (c) PNC Financial Services. All rights reserved.



namespace Dse.Tests;

public class UnitTest1(ITestOutputHelper toh, TestFixture fixture)
    : TestBed(toh, fixture)
{
    [Fact]
    public void Test1() =>
        Assert.True(true);
}
