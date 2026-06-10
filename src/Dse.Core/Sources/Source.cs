// Copyright (c) PNC Financial Services. All rights reserved.


using System.Text.RegularExpressions;
using Dse.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Thinktecture;

namespace Dse.Sources;

/// <summary>
///     Key used to identify a Source.
/// </summary>
[ValueObject<string>(
    UnsafeConversionToKeyMemberType = ConversionOperatorsGeneration.Implicit,
    ConversionFromKeyMemberType = ConversionOperatorsGeneration.Implicit,
    ConversionToKeyMemberType = ConversionOperatorsGeneration.Implicit
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
            validationError = new ValidationError("Must be 1-30 chars, lowercase alphanumeric, and start with a letter.");
        }
    }
}

public sealed class Source : Entity<SourceKey>
{
    private Source() { }
    private Source(SourceKey key) : base(key) { }
    public string AssemblyQualifiedName { get; private init; } = null!;
    public SourceModule GetModule() => Type.GetType(AssemblyQualifiedName)!.GetRequiredSourceModule();

    public static Source FromType(Type type)
    {
        SourceModule module = type.GetRequiredSourceModule();
        return new Source(module.SourceKey)
        {
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
