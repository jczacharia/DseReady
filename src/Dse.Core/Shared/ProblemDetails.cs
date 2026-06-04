// Copyright (c) PNC Financial Services. All rights reserved.


using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dse.Shared;

public static class ProblemDetailsExtensions
{
    public const string HttpContextKey = "SetProblemDetails";

    private static string BuildExceptionChainMessage(Exception ex)
    {
        StringBuilder message = new($"{ex.GetType().Name}: {ex.Message} {ex.StackTrace}");
        for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            message.Append($" {inner.Message}");
        }

        return message.ToString();
    }

    public static void ApplyCoreCustomization(this ProblemDetailsOptions setup) => setup.CustomizeProblemDetails = context =>
    {
        if (context.HttpContext.Items[HttpContextKey] is ProblemDetails setProblem)
        {
            context.ProblemDetails = setProblem;

            if (setProblem.Status is { } status && !context.HttpContext.Response.HasStarted)
            {
                context.HttpContext.Response.StatusCode = status;
            }

            return;
        }

        if (context.Exception is { } ex)
        {
            if (context.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>().IsProduction())
            {
                context.ProblemDetails.Detail =
                    "An exception occurred while processing your request."
                    + " Please try again later or contact the DSE team if the problem persists.";
                return;
            }

            context.ProblemDetails.Detail = BuildExceptionChainMessage(ex);
            return;
        }

        if (context.ProblemDetails is HttpValidationProblemDetails h)
        {
            h.Errors = h.Errors.ToDictionary(x => x.Key, x => x.Value);
        }
        else if (context.ProblemDetails is ValidationProblemDetails v)
        {
            v.Errors = v.Errors.ToDictionary(x => x.Key, x => x.Value);
        }

        if (context.ProblemDetails.Detail is not null)
        {
            return;
        }

        if (context.HttpContext.Response is { StatusCode: StatusCodes.Status404NotFound, HasStarted: false })
        {
            context.ProblemDetails.Detail = "The requested resource was not found.";
            context.ProblemDetails.Extensions["Path"] = context.HttpContext.Request.Path;
        }
    };

    public static void SetProblem(this HttpContext httpContext, ProblemDetails problem) =>
        httpContext.Items[HttpContextKey] = problem;

    public static void SetProblem(this HttpContext httpContext, HttpStatusCode statusCode, string title, string detail) =>
        httpContext.SetProblem(new ProblemDetails
        {
            Instance = httpContext.Request.Path,
            Title = title,
            Detail = detail,
            Status = (int)statusCode,
        });

    public static ProblemHttpResult ProblemHttpResult(
        this HttpContext httpContext,
        HttpStatusCode statusCode,
        string title,
        string detail) => TypedResults.Problem(new ProblemDetails
    {
        Instance = httpContext.Request.Path,
        Title = title,
        Detail = detail,
        Status = (int)statusCode,
    });
}
