// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Ingestion.Endpoints;
using Dse.Shared;
using Dse.Sources;
using Dse.Sources.Spec;
using Elastic.Mapping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Attributes;

[assembly: SourceManifest<Spec>]
[assembly: WolverineModule]

namespace Dse.Sources.Spec;

public sealed class Spec() : SourceModule<SpecDoc>("spec")
{
    public override ElasticsearchTypeContext GetTypeContext(IHostEnvironment env) => env.IsTest()
        ? SpecContext.SpecDocTest.CreateContext(Guid.NewGuid().ToString())
        : SpecContext.SpecDoc.Context with { IndexPatternUseBatchDate = true };

    public override void Register(SourceBuilder<SpecDoc> builder)
    {
        builder.Services.AddSingleton<SpecState>();
        builder.AddIngestion<SpecIngest>();
    }

    public override void Configure(SourcePipelineBuilder builder)
    {
        builder.MapIngestEndpoint();
        builder.MapDryIngestEndpoint();
        builder.MapGetIngestRunEndpoint();
        builder.MapCancelIngestRunEndpoint();
    }
}
