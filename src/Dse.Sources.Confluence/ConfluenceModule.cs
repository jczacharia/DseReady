// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Ingestion;
using Dse.Ingestion.Endpoints;
using Dse.Shared;
using Elastic.Mapping;
using Microsoft.AspNetCore.Builder;

namespace Dse.Sources.Confluence;

public sealed class Confluence() : SourceModule<ConfluenceDoc>("confluence")
{
    /// <summary>The AD group DN that gates Confluence search.</summary>
    public const string AdEntitlementDn = "CN=GSGu_CFL_CFLUsers,OU=OUg_Applications,OU=OUc_AccessGroups,DC=pncbank,DC=com";

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
        builder
            .MapSearchEndpoint()
            .RequireAuthorization(p => p.RequireRole(AdEntitlementDn));

        builder.MapIngestEndpoints();
        builder.MapGetIngestRunEndpoint();
        builder.MapCancelIngestRunEndpoint();
    }
}
