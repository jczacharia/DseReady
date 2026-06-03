// Copyright (c) PNC Financial Services. All rights reserved.


using AwesomeAssertions;
using Dse.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Dse.Tests.Shared;

public sealed class ProblemDetailsExtensionsTests
{
    /// <summary>The configured customization delegate — the actual unit under test.</summary>
    private static Action<ProblemDetailsContext> Customize
    {
        get
        {
            ProblemDetailsOptions options = new();
            options.ApplyCoreCustomization();
            return options.CustomizeProblemDetails!;
        }
    }

    private static ProblemDetailsContext Context(
        string env = "Development",
        ProblemDetails? problemDetails = null,
        Exception? exception = null,
        int responseStatus = StatusCodes.Status200OK,
        ProblemDetails? overriden = null)
    {
        ServiceCollection services = [];
        services.AddSingleton<IHostEnvironment>(new FakeEnv(env));
        DefaultHttpContext http = new()
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { StatusCode = responseStatus },
        };
        if (overriden is not null)
        {
            http.Items[ProblemDetailsExtensions.HttpContextKey] = overriden;
        }

        return new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = problemDetails ?? new ProblemDetails(),
            Exception = exception,
        };
    }

    [Fact]
    public void Overriden_ReplacesProblemDetailsAndAppliesStatus()
    {
        ProblemDetails overriden = new() { Status = StatusCodes.Status418ImATeapot, Title = "override" };
        ProblemDetailsContext ctx = Context(overriden: overriden);

        Customize(ctx);

        ctx.ProblemDetails.Should().BeSameAs(overriden);
        ctx.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status418ImATeapot);
    }

    [Fact]
    public void Exception_InProduction_HidesDetailsBehindGenericMessage()
    {
        ProblemDetailsContext ctx = Context("Production", exception: new InvalidOperationException("secret internals"));

        Customize(ctx);

        ctx.ProblemDetails.Detail.Should().StartWith("An exception occurred");
        ctx.ProblemDetails.Detail.Should().NotContain("secret internals");
    }

    [Fact]
    public void Exception_OutsideProduction_IncludesTypeAndInnerChain()
    {
        Exception ex = new InvalidOperationException("outer boom", new ArgumentException("inner cause"));
        ProblemDetailsContext ctx = Context(exception: ex);

        Customize(ctx);

        ctx.ProblemDetails.Detail.Should().Contain(nameof(InvalidOperationException));
        ctx.ProblemDetails.Detail.Should().Contain("outer boom");
        ctx.ProblemDetails.Detail.Should().Contain("inner cause", "the inner-exception chain is appended");
    }

    [Fact]
    public void HttpValidationProblemDetails_RebuildsErrorsDictionary()
    {
        HttpValidationProblemDetails pd = new(new Dictionary<string, string[]> { ["field"] = ["is required"] });
        ProblemDetailsContext ctx = Context(problemDetails: pd);

        Customize(ctx);

        pd.Errors.Should().ContainKey("field");
    }

    [Fact]
    public void ValidationProblemDetails_RebuildsErrorsDictionary()
    {
        ValidationProblemDetails pd = new(new Dictionary<string, string[]> { ["name"] = ["too long"] });
        ProblemDetailsContext ctx = Context(problemDetails: pd);

        Customize(ctx);

        pd.Errors.Should().ContainKey("name");
    }

    [Fact]
    public void NotFoundResponse_WithNoDetail_GetsResourceNotFoundMessage()
    {
        ProblemDetailsContext ctx = Context(responseStatus: StatusCodes.Status404NotFound);

        Customize(ctx);

        ctx.ProblemDetails.Detail.Should().Be("The requested resource was not found.");
    }

    [Fact]
    public void ExistingDetail_IsLeftUntouched()
    {
        ProblemDetailsContext ctx = Context(
            problemDetails: new ProblemDetails { Detail = "already set" },
            responseStatus: StatusCodes.Status404NotFound);

        Customize(ctx);

        ctx.ProblemDetails.Detail.Should().Be("already set");
    }

    [Fact]
    public void ProblemHttpResult_BuildsProblemFromStatusTitleDetail()
    {
        DefaultHttpContext http = new();
        http.Request.Path = "/api/widgets/7";

        ProblemHttpResult result = http.ProblemHttpResult(HttpStatusCode.Conflict, "Conflict", "Already exists");

        result.ProblemDetails.Status.Should().Be((int)HttpStatusCode.Conflict);
        result.ProblemDetails.Title.Should().Be("Conflict");
        result.ProblemDetails.Detail.Should().Be("Already exists");
        result.ProblemDetails.Instance.Should().Be("/api/widgets/7");
    }

    private sealed class FakeEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
