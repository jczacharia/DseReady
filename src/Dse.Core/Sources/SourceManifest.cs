// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;

namespace Dse.Sources;

[ExcludeFromCodeCoverage]
[AttributeUsage(AttributeTargets.Assembly)]
public abstract class SourceManifestAttribute(Type moduleType) : Attribute
{
    public SourceModule Module { get; } =
        Activator.CreateInstance(moduleType) as SourceModule ?? throw new ArgumentException(
            $"Type {moduleType} does not implement {nameof(SourceModule)}.");
}

[ExcludeFromCodeCoverage]
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class SourceManifestAttribute<TModule>()
    : SourceManifestAttribute(typeof(TModule)) where TModule : SourceModule, new();
