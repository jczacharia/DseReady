// Copyright (c) PNC Financial Services. All rights reserved.


using System.Reflection;
using Elastic.Mapping;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Dse.Sources;

[AttributeUsage(AttributeTargets.Assembly)]
public abstract class SourceModuleAttribute(Type moduleType) : Attribute
{
    public SourceModule Module { get; } =
        Activator.CreateInstance(moduleType) as SourceModule ?? throw new ArgumentException(
            $"Type {moduleType} does not implement {nameof(SourceModule)}.");
}

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class SourceModuleAttribute<TModule>()
    : SourceModuleAttribute(typeof(TModule)) where TModule : SourceModule, new();

public abstract class SourceModule(string key)
{
    public Assembly Assembly => GetType().Assembly;
    public SourceKey SourceKey { get; } = SourceKey.Create(key);
    public abstract ElasticsearchTypeContext GetTypeContext(DseEnv env);
    public abstract void Build(SourceBuilder builder);
    public virtual void ExtendSearchEndpoint(RouteHandlerBuilder builder) { }
}

public static class SourceModuleExtensions
{
    public static SourceModule GetAssemblySourceModule(this Type type) =>
        type.Assembly.GetCustomAttribute<SourceModuleAttribute>()?.Module
        ?? throw new InvalidOperationException($"Assembly {type.Assembly} does not have a {nameof(SourceModuleAttribute)}.");

    public static SourceKey GetAssemblySourceKey(this Type type) => type.GetAssemblySourceModule().SourceKey;

    public static void AddSourceModule<TModule>(this IServiceCollection services) where TModule : SourceModule
    {
        if (typeof(TModule).Assembly.GetCustomAttribute<SourceModuleAttribute>() is { Module: { } module })
        {
            module.Build(new SourceBuilder(module, services));
        }
        else
        {
            throw new InvalidOperationException(
                $"Assembly {typeof(TModule).Assembly} does not have a {nameof(SourceModuleAttribute)}.");
        }
    }
}
