// Copyright (c) PNC Financial Services. All rights reserved.


using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Dse.Sources;

/// <summary>
///     The execution surface a source module maps onto. Rooted at <c>sources/{sourceKey}</c>; a module may map
///     anything it likes beneath that prefix but, being a route group, can never escape it.
/// </summary>
public sealed class SourcePipelineBuilder : IEndpointRouteBuilder, IEndpointConventionBuilder
{
    private readonly RouteGroupBuilder _group;

    internal SourcePipelineBuilder(
        IEndpointRouteBuilder sources,
        SourceKey sourceKey)
    {
        SourceKey = sourceKey;
        _group = sources.MapGroup(sourceKey);
    }

    public SourceKey SourceKey { get; }
    private IEndpointRouteBuilder Endpoints => _group;

    public void Add(Action<EndpointBuilder> convention) => ((IEndpointConventionBuilder)_group).Add(convention);
    public void Finally(Action<EndpointBuilder> finalConvention) => ((IEndpointConventionBuilder)_group).Finally(finalConvention);

    public IServiceProvider ServiceProvider => Endpoints.ServiceProvider;
    ICollection<EndpointDataSource> IEndpointRouteBuilder.DataSources => Endpoints.DataSources;
    IApplicationBuilder IEndpointRouteBuilder.CreateApplicationBuilder() => Endpoints.CreateApplicationBuilder();
}
