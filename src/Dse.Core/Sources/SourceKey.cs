// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
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
