// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Ingestion.Endpoints;
using Dse.Shared;
using Dse.Sources;
using Dse.Sources.Confluence;
using Elastic.Mapping;
using Microsoft.AspNetCore.Builder;
using Wolverine.Attributes;

[assembly: SourceManifest<Confluence>]
[assembly: WolverineModule]

namespace Dse.Sources.Confluence;

public sealed class Confluence() : SourceModule<ConfluenceDoc>("confluence")
{
    public override ElasticsearchTypeContext GetTypeContext(IDseEnvironment dseEnv) => dseEnv.IsTest()
        ? ConfluenceContext.ConfluenceDocTest.CreateContext(Guid.NewGuid().ToString())
        : ConfluenceContext.ConfluenceDoc.Context with { IndexPatternUseBatchDate = true };

    public override void Register(SourceBuilder<ConfluenceDoc> builder)
    {
        builder.Services
            .AddFluentOptions<ConfluenceOptions>(ConfluenceOptions.SectionName)
            .PostConfigure<IDseEnvironment>(static (o, env) =>
            {
                o.BaseAddress = o.BaseAddress.Or("https://confluence.pncint.net");

                if (env is IDseLocalEnvironment localEnv)
                {
                    o.Username = o.Username.Or(localEnv.Username);
                    o.Password = o.Password.Or(localEnv.Password);
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
