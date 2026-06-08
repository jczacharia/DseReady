// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Serialization;
using Elastic.Mapping;
using Elastic.Mapping.Mappings;

namespace Dse.Sources.Confluence;

[ExcludeFromCodeCoverage]
public sealed class ConfluenceDoc
{
    [Id]
    [Keyword]
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [ContentHash]
    [Keyword]
    [JsonPropertyName("hash")]
    public string Hash => VersionNumber.ToString(CultureInfo.InvariantCulture);

    [Keyword]
    [JsonPropertyName("type")]
    public required string? Type { get; set; }

    [Keyword]
    [JsonPropertyName("title")]
    public required string? Title { get; set; }

    [Text]
    [JsonPropertyName("body")]
    public required string? Body { get; set; }

    [Object]
    [JsonPropertyName("space")]
    public required SpaceRecord? Space { get; set; }

    [Long]
    [JsonPropertyName("versionNumber")]
    public required long VersionNumber { get; set; }

    [LastUpdated]
    [Timestamp]
    [Date]
    [JsonPropertyName("versionWhen")]
    public required DateTimeOffset VersionWhen { get; set; }

    [Object]
    [JsonPropertyName("versionBy")]
    public required User VersionBy { get; set; }

    [Date]
    [JsonPropertyName("createdDate")]
    public required DateTimeOffset CreatedDate { get; set; }

    [Object]
    [JsonPropertyName("createdBy")]
    public required User CreatedBy { get; set; }

    [Nested]
    [JsonPropertyName("ancestors")]
    public required Ancestor[] Ancestors { get; set; }

    [Nested]
    [JsonPropertyName("labels")]
    public required Label[] Labels { get; set; }

    [BatchIndexDate]
    [Date]
    [JsonPropertyName("batchIndexDate")]
    public DateTimeOffset BatchIndexDate { get; set; } = DateTimeOffset.UtcNow;

    public sealed record SpaceRecord
    {
        [Long]
        [JsonPropertyName("id")]
        public required long Id { get; set; }

        [Keyword]
        [JsonPropertyName("key")]
        public required string Key { get; set; }

        [Keyword]
        [JsonPropertyName("name")]
        public required string? Name { get; set; }

        [Keyword(Index = false)]
        [JsonPropertyName("link")]
        public required string? Link { get; set; }
    }

    public sealed record User
    {
        [Keyword]
        [JsonPropertyName("username")]
        public required string? Username { get; set; }

        [Keyword]
        [JsonPropertyName("userKey")]
        public required string? UserKey { get; set; }

        [Keyword]
        [JsonPropertyName("displayName")]
        public required string? DisplayName { get; set; }

        [Keyword(Index = false)]
        [JsonPropertyName("profilePicturePath")]
        public required string? ProfilePicturePath { get; set; }
    }

    public sealed record Ancestor
    {
        [Keyword]
        [JsonPropertyName("id")]
        public required string Id { get; set; }

        [Keyword]
        [JsonPropertyName("type")]
        public required string? Type { get; set; }

        [Keyword]
        [JsonPropertyName("title")]
        public required string? Title { get; set; }

        [Keyword(Index = false)]
        [JsonPropertyName("link")]
        public required string? Link { get; set; }
    }

    public sealed record Label
    {
        [Keyword]
        [JsonPropertyName("id")]
        public required string Id { get; set; }

        [Keyword]
        [JsonPropertyName("name")]
        public required string? Name { get; set; }

        [Keyword]
        [JsonPropertyName("prefix")]
        public required string? Prefix { get; set; }
    }
}

[ElasticsearchMappingContext]
[Index<ConfluenceDoc>(
    Name = "source-confluence",
    WriteAlias = "source-confluence",
    ReadAlias = "source-confluence-search",
    DatePattern = "yyyy.MM.dd.HHmmss",
    RefreshInterval = "30s",
    Configuration = typeof(ConfluenceDocConfiguration)
)]
[Index<ConfluenceDoc>(
    NameTemplate = "test-confluence-{uuid}",
    RefreshInterval = "-1",
    Variant = "Test",
    Configuration = typeof(ConfluenceDocConfiguration)
)]
public static partial class ConfluenceContext;

public sealed class ConfluenceDocConfiguration : SourceDocOptions<ConfluenceDoc>
{
    public override MappingsBuilder<ConfluenceDoc> ConfigureMappings(MappingsBuilder<ConfluenceDoc> mappings) =>
        mappings
            .Type(b => b.DseKeyword())
            .Title(b => b.DseKeyword())
            .Body(b => b.DseText())
            .Space(b => b.Key(k => k.DseKeyword()).Name(n => n.DseKeyword()))
            .VersionBy(v => v.DisplayName(d => d.DseKeyword()).Username(u => u.DseKeyword()))
            .CreatedBy(c => c.DisplayName(d => d.DseKeyword()).Username(u => u.DseKeyword()))
            .Ancestors(a => a.Title(t => t.DseKeyword()))
            .Labels(l => l.Name(n => n.DseKeyword()));
}
