// Copyright (c) PNC Financial Services. All rights reserved.


using System.Globalization;
using System.Text.Json.Serialization;
using Elastic.Mapping;
using Elastic.Mapping.Mappings;

namespace Dse.Sources.Spec;

public sealed class SpecDoc
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
    [JsonPropertyName("keyword")]
    public required string Keyword { get; set; }

    [Text]
    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [Long]
    [JsonPropertyName("versionNumber")]
    public required long VersionNumber { get; set; }

    [LastUpdated]
    [Timestamp]
    [Date]
    [JsonPropertyName("@timestamp")]
    public required DateTimeOffset Timestamp { get; set; }


    [BatchIndexDate]
    [Date]
    [JsonPropertyName("batchIndexDate")]
    public DateTimeOffset BatchIndexDate { get; set; } = DateTimeOffset.UtcNow;
}

[ElasticsearchMappingContext]
[Index<SpecDoc>(
    Name = "source-spec",
    WriteAlias = "source-spec",
    ReadAlias = "source-spec-search",
    DatePattern = "yyyy.MM.dd.HHmmss",
    RefreshInterval = "30s",
    Configuration = typeof(SpecDocConfiguration)
)]
[Index<SpecDoc>(
    NameTemplate = "test-spec-{uuid}",
    RefreshInterval = "-1",
    Variant = "Test",
    Configuration = typeof(SpecDocConfiguration)
)]
public static partial class SpecContext;

public sealed class SpecDocConfiguration : SourceDocOptions<SpecDoc>
{
    public override MappingsBuilder<SpecDoc> ConfigureMappings(MappingsBuilder<SpecDoc> mappings) => mappings
        .Keyword(b => b.DseKeyword())
        .Text(b => b.DseText());
}
