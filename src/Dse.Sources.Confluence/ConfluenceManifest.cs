// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using Dse.Sources;
using Dse.Sources.Confluence;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Attributes;

[assembly: SourceManifest<Confluence>]
[assembly: WolverineModule]

namespace Dse.Sources.Confluence;

[ExcludeFromCodeCoverage]
public static class SourceManifestExtensions
{
    public static void AddSourceManifest<T>(this IServiceCollection services)
    {
        SourceModule module = typeof(T).GetRequiredSourceModule();
        module.Register(services);
    }
}
