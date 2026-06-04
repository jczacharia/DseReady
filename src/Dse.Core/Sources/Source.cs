// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Dse.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Thinktecture;

namespace Dse.Sources;

/// <summary>
///     Key used to identify a Source.
/// </summary>
[ExcludeFromCodeCoverage]
[ValueObject<string>(
    UnsafeConversionToKeyMemberType = ConversionOperatorsGeneration.Explicit,
    ConversionFromKeyMemberType = ConversionOperatorsGeneration.Explicit,
    ConversionToKeyMemberType = ConversionOperatorsGeneration.Explicit
)]
[KeyMemberEqualityComparer<ComparerAccessors.StringOrdinalIgnoreCase, string>]
[KeyMemberComparer<ComparerAccessors.StringOrdinalIgnoreCase, string>]
public sealed partial class SourceKey
{
    [GeneratedRegex("^[a-z][a-z0-9-]{0,29}$")]
    private static partial Regex Pattern();

    static partial void ValidateFactoryArguments(ref ValidationError? validationError, ref string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            validationError = new ValidationError("SourceKey cannot be empty");
            return;
        }

        value = value.Trim();

        if (!Pattern().IsMatch(value))
        {
            validationError =
                new ValidationError("Invalid SourceKey. Must be 1-30 chars, lowercase alphanumeric, and start with a letter.");
        }
    }
}

public sealed class Source : Entity<SourceKey>
{
    public override required SourceKey Id { get; init; }
    public string AssemblyQualifiedName { get; private init; } = null!;
    public SourceModule GetModule() => Type.GetType(AssemblyQualifiedName)!.GetRequiredSourceModule();

    private Source() { }

    public static Source FromType(Type type)
    {
        SourceModule module = type.GetRequiredSourceModule();
        return new Source
        {
            Id = module.SourceKey,
            AssemblyQualifiedName =
                module.GetType().AssemblyQualifiedName ??
                throw new InvalidOperationException($"AssemblyQualifiedName is null for type {module.GetType()}"),
        };
    }

    public static Source FromModule(SourceModule module) => FromType(module.GetType());
}

internal sealed class SourceConfiguration : IEntityTypeConfiguration<Source>
{
    public void Configure(EntityTypeBuilder<Source> builder) => builder.ToTable(nameof(Source));
}
