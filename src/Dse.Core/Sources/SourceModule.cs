// Copyright (c) PNC Financial Services. All rights reserved.


using System.Reflection;
using Elastic.Mapping;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dse.Sources;

public abstract class SourceModule
{
    private protected SourceModule(string key) => SourceKey = SourceKey.Create(key);

    public Assembly Assembly => GetType().Assembly;
    public SourceKey SourceKey { get; }
    public abstract ElasticsearchTypeContext GetTypeContext(IHostEnvironment env);
    public abstract void Register(IServiceCollection services);
    public abstract void Configure(IEndpointRouteBuilder app);
}

public abstract class SourceModule<TDoc>(string key) : SourceModule(key) where TDoc : class
{
    public abstract void Register(SourceBuilder<TDoc> builder);
    public sealed override void Register(IServiceCollection services) => Register(new SourceBuilder<TDoc>(this, services));

    public abstract void Configure(SourcePipelineBuilder builder);

    public sealed override void Configure(IEndpointRouteBuilder app) => Configure(new SourcePipelineBuilder(app, SourceKey));
}

public static class SourceModuleExtensions
{
    public static SourceModule? GetSourceModule(this Type type) =>
        type.Assembly.GetCustomAttribute<SourceManifestAttribute>()?.Module;

    public static SourceModule? GetSourceModule(this object obj) =>
        obj.GetType().GetSourceModule();

    public static SourceModule GetRequiredSourceModule(this Type type) =>
        type.GetSourceModule()
        ?? throw new InvalidOperationException($"Assembly {type.Assembly} does not have a {nameof(SourceManifestAttribute)}.");

    public static SourceModule GetRequiredSourceModule(this object obj) =>
        obj.GetType().GetRequiredSourceModule();

    public static SourceKey? GetSourceKey(this Type type) => type.GetSourceModule()?.SourceKey;
    public static SourceKey? GetSourceKey(this object obj) => obj.GetType().GetSourceKey();

    public static SourceKey GetRequiredSourceKey(this Type type) => type.GetRequiredSourceModule().SourceKey;
    public static SourceKey GetRequiredSourceKey(this object obj) => obj.GetType().GetRequiredSourceKey();

    public static ElasticsearchTypeContext? GetTypeContext(this Type type, IServiceProvider sp) =>
        type.GetSourceModule()?.GetTypeContext(sp);

    public static ElasticsearchTypeContext? GetTypeContext(this object obj, IServiceProvider sp) =>
        obj.GetType().GetTypeContext(sp);

    public static ElasticsearchTypeContext GetRequiredTypeContext(this Type type, IServiceProvider sp) =>
        type.GetRequiredSourceModule().GetTypeContext(sp.GetRequiredService<IHostEnvironment>());

    public static ElasticsearchTypeContext GetRequiredTypeContext(this object obj, IServiceProvider sp) =>
        obj.GetType().GetRequiredTypeContext(sp);
}
