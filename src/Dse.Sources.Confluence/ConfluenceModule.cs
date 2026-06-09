// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Ingestion.Endpoints;
using Dse.Shared;
using Dse.Sources;
using Dse.Sources.Confluence;
using Elastic.Mapping;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Attributes;

[assembly: SourceManifest<Confluence>]
[assembly: WolverineModule]

namespace Dse.Sources.Confluence;

public sealed class Confluence() : SourceModule<ConfluenceDoc>("confluence")
{
    public override ElasticsearchTypeContext GetTypeContext(IHostEnvironment env) => env.IsTest()
        ? ConfluenceContext.ConfluenceDocTest.CreateContext(Guid.NewGuid().ToString())
        : ConfluenceContext.ConfluenceDoc.Context with { IndexPatternUseBatchDate = true };

    public override void Register(SourceBuilder<ConfluenceDoc> builder)
    {
        builder.Services
            .AddOptions<ConfluenceOptions>()
            .BindConfiguration(ConfluenceOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart()
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
        builder.MapIngestEndpoints();
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
