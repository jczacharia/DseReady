// Copyright (c) PNC Financial Services. All rights reserved.


namespace Dse.Confluence.Seeder;

// Bound from CLI args (--Confluence:BaseUrl=...), env vars (SEEDER_*), then appsettings.json.
public sealed class SeederOptions
{
    public ConfluenceConnection Confluence { get; set; } = new();
    public PostgresConnection Postgres { get; set; } = new();
    public CorpusOptions Corpus { get; set; } = new();

    // Same seed + empty instance => same corpus.
    public int RandomSeed { get; set; } = 1337;

    // Optional Postgres pass to back-date timestamps (REST always stamps "now"). Needs a re-index after.
    public bool BackdateViaPostgres { get; set; }

    public sealed class ConfluenceConnection
    {
        public string BaseUrl { get; set; } = "http://localhost:8090";
        public string AdminUsername { get; set; } = "admin";

        public string AdminPassword { get; set; } = "admin";

        // Shared password for the author roster; authoring as each user varies createdBy/version.by.
        public string AuthorPassword { get; set; } = "P@ssw0rd123";
        public int MaxConcurrency { get; set; } = 6;
    }

    public sealed class PostgresConnection
    {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 46247;
        public string Database { get; set; } = "confluencedb";
        public string Username { get; set; } = "confluencedb";
        public string Password { get; set; } = "jellyfish";

        public string ConnectionString =>
            $"Host={Host};Port={Port};Database={Database};Username={Username};Password={Password}";
    }

    public sealed class CorpusOptions
    {
        // Extra synthetic leaves per space; default pushes total content past the 50-result CQL page.
        public int SyntheticPagesPerSpace { get; set; } = 28;

        public int BlogPostsPerSpace { get; set; } = 4;

        // Fraction of pages that get extra versions.
        public double MultiVersionRatio { get; set; } = 0.35;
        public bool UploadAttachments { get; set; } = true;
    }
}
