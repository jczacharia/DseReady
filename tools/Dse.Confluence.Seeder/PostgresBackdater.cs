// Copyright (c) PNC Financial Services. All rights reserved.


using Npgsql;

namespace Dse.Confluence.Seeder;

// Optional: REST stamps everything "now". This spreads creation/modified dates across history directly in
// Confluence's DB. Caches/Lucene won't reflect it until a reindex or container restart (see README).
public sealed class PostgresBackdater(SeederOptions options, IReadOnlyCollection<string> spaceKeys)
{
    public async Task BackdateAsync()
    {
        await using var conn = new NpgsqlConnection(options.Postgres.ConnectionString);
        await conn.OpenAsync();

        string keys = string.Join(",", spaceKeys.Select(k => $"'{k.Replace("'", "''")}'"));
        const string sql = """
                           update content c set
                               creationdate = now() - (random() * interval '600 days') - interval '120 days',
                               lastmoddate  = now() - (random() * interval '120 days')
                           from spaces s
                           where c.spaceid = s.spaceid
                             and s.spacekey in ({KEYS})
                             and c.contenttype in ('PAGE','BLOGPOST')
                             and c.content_status = 'current';
                           """;

        await using var cmd = new NpgsqlCommand(sql.Replace("{KEYS}", keys), conn);
        int rows = await cmd.ExecuteNonQueryAsync();
        Console.WriteLine($"Backdated {rows} content rows across [{keys}].");
        Console.WriteLine("NOTE: restart the Confluence container (or run a content re-index) before ingesting,");
        Console.WriteLine("      so the new dates flush from cache and into Lucene.");
    }
}
