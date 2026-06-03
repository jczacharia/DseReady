// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dse.Shared;

public interface IEndpoint
{
    void MapEndpoint(IEndpointRouteBuilder builder);
}

public static class EndpointExtensions
{
    public static IServiceCollection AddEndpoints(this IServiceCollection services, Assembly[] assemblies)
    {
        ServiceDescriptor[] endpointServiceDescriptors = assemblies
            .SelectMany(a => a.DefinedTypes)
            .Where(type => type is { IsAbstract: false, IsInterface: false } && type.IsAssignableTo(typeof(IEndpoint)))
            .Select(type => ServiceDescriptor.Transient(typeof(IEndpoint), type))
            .ToArray();

        services.TryAddEnumerable(endpointServiceDescriptors);

        return services;
    }

    public static IEndpointConventionBuilder MapEndpoints(
        this IEndpointRouteBuilder root,
        [StringSyntax("Route")] string pattern = "")
    {
        RouteGroupBuilder endpointsGroup = root.MapGroup(pattern);

        foreach (IEndpoint endpoint in root.ServiceProvider.GetRequiredService<IEnumerable<IEndpoint>>())
        {
            endpoint.MapEndpoint(endpointsGroup);
        }

        return endpointsGroup;
    }
}
