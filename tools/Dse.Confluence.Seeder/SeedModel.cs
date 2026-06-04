// Copyright (c) PNC Financial Services. All rights reserved.


namespace Dse.Confluence.Seeder;

public sealed record Author(string Username, string DisplayName);

public sealed class SeedSpace
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public List<SeedPage> Pages { get; } = [];
}

public sealed record SeedAttachment(string FileName, byte[] Content, string ContentType, string Comment);

// Children become descendants (populates ancestors[]); ExtraVersions bump version.number.
public sealed class SeedPage
{
    public required string Title { get; init; }
    public string Type { get; init; } = "page";
    public required string BodyStorage { get; init; }
    public Author? Author { get; init; }
    public List<string> Labels { get; } = [];
    public List<SeedAttachment> Attachments { get; } = [];
    public List<(string BodyStorage, Author? Author)> ExtraVersions { get; } = [];
    public List<SeedPage> Children { get; } = [];
    public string? Id { get; set; }
}
