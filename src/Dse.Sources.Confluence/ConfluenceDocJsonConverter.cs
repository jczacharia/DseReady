// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dse.Sources.Confluence;

/// <summary>
///     Reads a Confluence content-search result <b>directly</b> into a <see cref="ConfluenceDoc" /> — no
///     intermediate API DTOs. The result object is parsed into a transient, pooled <see cref="JsonDocument" />
///     and navigated with <see cref="JsonElement" />, which is order-independent and impossible to
///     mis-position.
///     <para>
///         <b>Memory.</b> The <see cref="JsonDocument" /> is disposed before <c>Read</c> returns, so its
///         pooled buffers are returned to the <c>ArrayPool</c> and reused by the next document — bounded,
///         not accumulating. The only heap strings kept are the fields we map; the raw <c>body.storage.value</c>
///         markup is read, cleaned, and dropped — only the cleaned text rides on the document.
///     </para>
///     <para>
///         Read-only and registered on the client's deserialize options only (never as an attribute on
///         <see cref="ConfluenceDoc" />), so the document still serializes to Elasticsearch via its mapping
///         attributes. No <see cref="JsonElement" /> escapes the document's lifetime.
///     </para>
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class ConfluenceDocJsonConverter : JsonConverter<ConfluenceDoc>
{
    public static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        Converters = { new ConfluenceDocJsonConverter() },
    };

    public override ConfluenceDoc Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;

        JsonElement? version = Prop(root, "version");
        JsonElement? history = Prop(root, "history");
        string rawBody = Str(Prop(Prop(root, "body"), "storage"), "value") ?? string.Empty;

        return new ConfluenceDoc
        {
            Id = Str(root, "id") ?? string.Empty,
            Type = Str(root, "type"),
            Title = Str(root, "title"),
            Body = ConfluenceHtmlCleaner.Clean(rawBody),
            Space = MapSpace(Prop(root, "space")),
            VersionNumber = Int64(version, "number"),
            VersionWhen = Date(version, "when"),
            VersionBy = MapUser(Prop(version, "by")),
            CreatedDate = Date(history, "createdDate"),
            CreatedBy = MapUser(Prop(history, "createdBy")),
            Ancestors = MapAncestors(Prop(root, "ancestors")),
            Labels = MapLabels(Prop(Prop(root, "metadata"), "labels")),
        };
    }

    public override void Write(Utf8JsonWriter writer, ConfluenceDoc value, JsonSerializerOptions options) =>
        throw new NotSupportedException(
            $"{nameof(ConfluenceDocJsonConverter)} is read-only; it maps Confluence responses into {nameof(ConfluenceDoc)}.");

    private static ConfluenceDoc.SpaceRecord? MapSpace(JsonElement? space)
    {
        if (space is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        return new ConfluenceDoc.SpaceRecord
        {
            Id = Int64(element, "id"),
            Key = Str(element, "key") ?? string.Empty,
            Name = Str(element, "name"),
            Link = Str(Prop(element, "_links"), "webui"),
        };
    }

    private static ConfluenceDoc.User MapUser(JsonElement? user) => new()
    {
        Username = Str(user, "username"),
        UserKey = Str(user, "userKey"),
        DisplayName = Str(user, "displayName"),
        ProfilePicturePath = Str(Prop(user, "profilePicture"), "path"),
    };

    private static ConfluenceDoc.Ancestor[] MapAncestors(JsonElement? ancestors)
    {
        if (ancestors is not { ValueKind: JsonValueKind.Array } array)
        {
            return [];
        }

        return array
            .EnumerateArray()
            .Select(element => new ConfluenceDoc.Ancestor
            {
                Id = Str(element, "id") ?? string.Empty,
                Type = Str(element, "type"),
                Title = Str(element, "title"),
                Link = Str(Prop(element, "_links"), "webui"),
            })
            .ToArray();
    }

    private static ConfluenceDoc.Label[] MapLabels(JsonElement? labels)
    {
        if (Prop(labels, "results") is not { ValueKind: JsonValueKind.Array } array)
        {
            return [];
        }

        return array
            .EnumerateArray()
            .Select(element => new ConfluenceDoc.Label
            {
                Id = Str(element, "id") ?? string.Empty,
                Name = Str(element, "name"),
                Prefix = Str(element, "prefix"),
            })
            .ToArray();
    }

    // ---- Safe, null-chaining JsonElement navigation. ----

    /// <summary>The named child if <paramref name="element" /> is an object that has it; otherwise null.</summary>
    private static JsonElement? Prop(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out JsonElement value)
            ? value
            : null;

    /// <summary>The string value of <paramref name="element" />'s named child, or null.</summary>
    private static string? Str(JsonElement? element, string name) =>
        Prop(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static long Int64(JsonElement? element, string name) =>
        Prop(element, name) is { ValueKind: JsonValueKind.Number } value ? value.GetInt64() : 0;

    private static DateTimeOffset Date(JsonElement? element, string name) =>
        Prop(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetDateTimeOffset() : default;
}

/// <summary>
///     The pagination envelope — the only intermediate type left. Each result is mapped straight to a
///     <see cref="ConfluenceDoc" /> by the registered converter.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed record ConfluenceSearchResponse(
    [property: JsonPropertyName("results")] IReadOnlyList<ConfluenceDoc> Results,
    [property: JsonPropertyName("totalSize")] long TotalSize);
