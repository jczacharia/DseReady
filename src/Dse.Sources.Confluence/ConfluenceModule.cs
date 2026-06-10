// Copyright (c) PNC Financial Services. All rights reserved.


using System.ComponentModel.DataAnnotations;
using Dse.Ingestion.Endpoints;
using Dse.Shared;
using Dse.Sources;
using Dse.Sources.Confluence;
using Elastic.Mapping;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Wolverine.Attributes;

[assembly: SourceManifest<Confluence>]
[assembly: WolverineModule]

namespace Dse.Sources.Confluence;

public sealed class ConfluenceOptions
{
    public string BaseAddress { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? Proxy { get; set; }

    [Range(minimum: 1, maximum: 50)]
    public int PageSize { get; set; } = 50;

    public string ContentCql { get; set; } = "type in (page,blogpost) order by lastModified desc";

    public string[] ContentExpand { get; set; } =
    [
        "ancestors",
        "body.storage",
        "history",
        "metadata.labels",
        "space",
        "version",
    ];
}

public sealed class Confluence() : SourceModule<ConfluenceDoc>("confluence")
{
    public override ElasticsearchTypeContext GetTypeContext(IHostEnvironment env) => env.IsTest()
        ? ConfluenceContext.ConfluenceDocTest.CreateContext(Guid.NewGuid().ToString())
        : ConfluenceContext.ConfluenceDoc.Context with { IndexPatternUseBatchDate = true };

    public override void Register(SourceBuilder<ConfluenceDoc> builder)
    {
        builder.AddOptions<ConfluenceOptions>()
            .PostDseConfigure(static (o, dse) =>
            {
                if (dse.LocalCredentials() is { } cred)
                {
                    o.Username = o.Username.Or(cred.Username);
                    o.Password = o.Password.Or(cred.Password);
                }
            });

        builder.Services.AddConfluenceHttpClients();
        builder.AddHealthCheck<ConfluenceHealthCheck>();
        builder.AddIngestion<ConfluenceIngest>();
    }

    public override void Configure(SourcePipelineBuilder builder)
    {
        builder.MapIngestEndpoint();
        builder.MapDryIngestEndpoint();
        builder.MapGetIngestRunEndpoint();
        builder.MapCancelIngestRunEndpoint();

        builder
            .MapSearchEndpoint()
            .RequireAuthorization(p => p.RequireConfluenceEntitlement());

        builder
            .MapConfluenceBodyViewEndpoint()
            .RequireAuthorization(p => p.RequireConfluenceEntitlement());
    }
}
