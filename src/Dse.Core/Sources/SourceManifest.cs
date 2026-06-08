// Copyright (c) PNC Financial Services. All rights reserved.


using Microsoft.Extensions.DependencyInjection;

namespace Dse.Sources;

[AttributeUsage(AttributeTargets.Assembly)]
public abstract class SourceManifestAttribute(Type moduleType) : Attribute
{
    public SourceModule Module { get; } =
        Activator.CreateInstance(moduleType) as SourceModule ?? throw new ArgumentException(
            $"Type {moduleType} does not implement {nameof(SourceModule)}.");
}

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class SourceManifestAttribute<TModule>()
    : SourceManifestAttribute(typeof(TModule)) where TModule : SourceModule, new();

public static class SourceManifestExtensions
{
    public static void AddSourceManifest<T>(this IServiceCollection services)
    {
        SourceModule module = typeof(T).GetRequiredSourceModule();
        module.Register(services);
    }
}
