// Copyright (c) PNC Financial Services. All rights reserved.


using AwesomeAssertions;
using Dse.Shared;

namespace Dse.Tests.Shared;

public sealed class ExceptionDtoTests
{
    /// <summary>Throw + catch so the exception carries a real stack trace, as it would in production.</summary>
    private static Exception Thrown(Action act)
    {
        try
        {
            act();
        }
        catch (Exception e)
        {
            return e;
        }

        throw new InvalidOperationException("Expected the action to throw.");
    }

    [Fact]
    public void From_SimpleException_CapturesTypeMessageAndStackTrace()
    {
        Exception ex = Thrown(() => throw new InvalidOperationException("boom"));

        ExceptionDto dto = ExceptionDto.From(ex);

        dto.Type.Should().Be(typeof(InvalidOperationException).FullName);
        dto.Message.Should().Be("boom");
        dto.StackTrace.Should().NotBeNullOrEmpty();
        dto.InnerException.Should().BeNull();
        dto.InnerExceptions.Should().BeNull();
        dto.Data.Should().BeNull("no Data entries were added");
    }

    [Fact]
    public void From_NestedException_CapturesInnerRecursively()
    {
        ExceptionDto dto = ExceptionDto.From(
            new InvalidOperationException("outer", new ArgumentException("inner")));

        dto.InnerException.Should().NotBeNull();
        dto.InnerException!.Type.Should().Be(typeof(ArgumentException).FullName);
        dto.InnerException.Message.Should().Be("inner");
    }

    [Fact]
    public void From_AggregateException_CapturesEachInnerException()
    {
        ExceptionDto dto = ExceptionDto.From(
            new AggregateException(new Exception("a"), new Exception("b")));

        dto.InnerExceptions.Should().NotBeNull();
        dto.InnerExceptions!.Select(e => e.Message).Should().Equal("a", "b");
    }

    [Fact]
    public void From_WithData_CapturesOnlyStringKeyedEntries()
    {
        Exception ex = new("x");
        ex.Data["env"] = "prod";
        ex.Data[42] = "non-string-key-is-ignored";

        ExceptionDto dto = ExceptionDto.From(ex);

        dto.Data.Should().NotBeNull();
        dto.Data!["env"].Should().Be("prod");
        dto.Data.Should().NotContainKey("42");
    }

    [Fact]
    public void From_Null_Throws()
    {
        Action act = () => ExceptionDto.From(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
