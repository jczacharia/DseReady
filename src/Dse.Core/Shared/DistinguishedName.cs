// Copyright (c) PNC Financial Services. All rights reserved.


namespace Dse.Shared;

public readonly record struct DistinguishedName(string Value)
{
    public static DistinguishedName Parse(string dn) => new(dn);

    /// <summary>
    ///     Extracts the value for the first RDN matching <paramref name="attributeType" />.
    /// </summary>
    /// <example>
    ///     <code>
    /// var dn = DistinguishedName.Parse("cn=app-dse-kibana-admin,ou=Groups,o=pnc");
    /// dn.GetAttribute("cn") // "app-dse-kibana-admin"
    /// dn.GetAttribute("o")  // "pnc"
    /// </code>
    /// </example>
    public ReadOnlySpan<char> GetAttribute(ReadOnlySpan<char> attributeType)
    {
        ReadOnlySpan<char> remaining = Value.AsSpan();

        while (!remaining.IsEmpty)
        {
            int eq = remaining.IndexOf('=');
            if (eq < 0)
            {
                break;
            }

            if (remaining[..eq].Equals(attributeType, StringComparison.OrdinalIgnoreCase))
            {
                ReadOnlySpan<char> value = remaining[(eq + 1)..];
                int comma = value.IndexOf(',');
                return comma >= 0 ? value[..comma] : value;
            }

            // Advance past this RDN
            int next = remaining.IndexOf(',');
            if (next < 0)
            {
                break;
            }

            remaining = remaining[(next + 1)..];
        }

        return default;
    }

    /// <summary>Materializes the attribute value as a string. Use at boundaries only.</summary>
    public string? GetAttributeString(ReadOnlySpan<char> attributeType)
    {
        ReadOnlySpan<char> span = GetAttribute(attributeType);
        return span.IsEmpty ? null : span.ToString();
    }

    public override string ToString() => Value;
}
