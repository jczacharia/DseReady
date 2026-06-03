// Copyright (c) PNC Financial Services. All rights reserved.


using System.Text;
using AwesomeAssertions;
using Dse.Shared;

namespace Dse.Tests.Shared;

public sealed class UtilsTests
{
    [Fact]
    public void EncodeBasicAuth_EncodesColonJoinedCredentialsAsBase64()
    {
        string encoded = Utils.EncodeBasicAuth("admin", "s3cret");

        Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Should().Be("admin:s3cret");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EncodeBasicAuth_BlankUsername_Throws(string? username)
    {
        Action act = () => Utils.EncodeBasicAuth(username!, "p");
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EncodeBasicAuth_BlankPassword_Throws(string? password)
    {
        Action act = () => Utils.EncodeBasicAuth("u", password!);
        act.Should().Throw<ArgumentException>();
    }
}
