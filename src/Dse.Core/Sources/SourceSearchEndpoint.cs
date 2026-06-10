// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using Elastic.Clients.Elasticsearch;
using Elastic.Mapping;
using Elastic.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using HttpMethod = Elastic.Transport.HttpMethod;

namespace Dse.Sources;

public static class SourceSearchEndpoint
{
    public static RouteHandlerBuilder MapSearchEndpoint(
        this SourcePipelineBuilder builder,
        [StringSyntax("Route")] string pattern = "search") =>
        builder.MapPost(pattern, async Task<Results<ProblemHttpResult, EmptyHttpResult>> (
            ElasticsearchClient elastic,
            HttpContext ctx,
            IServiceProvider services,
            CancellationToken ct) =>
        {
            var typeCtx = services.GetRequiredKeyedService<ElasticsearchTypeContext>(builder.SourceKey);
            var response = await elastic.Transport.RequestAsync<StringResponse>(
                new EndpointPath(HttpMethod.POST, $"/{typeCtx.ResolveReadTarget()}/_search"),
                new CopyStreamPostData(ctx.Request.Body),
                ct);

            ctx.Response.ContentType = "application/json";
            ctx.Response.StatusCode = response.ApiCallDetails.HttpStatusCode ?? StatusCodes.Status502BadGateway;
            await ctx.Response.WriteAsync(response.Body, ct);

            return TypedResults.Empty;
        });

    private sealed class CopyStreamPostData(Stream source) : PostData
    {
        public override void Write(Stream writableStream, ITransportConfiguration settings, bool disableDirectStreaming) =>
            source.CopyTo(writableStream);

        public override Task WriteAsync(
            Stream writableStream,
            ITransportConfiguration settings,
            bool disableDirectStreaming,
            CancellationToken cancellationToken) => source.CopyToAsync(writableStream, cancellationToken);
    }
}
