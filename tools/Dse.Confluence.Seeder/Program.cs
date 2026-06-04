// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Confluence.Seeder;
using Microsoft.Extensions.Configuration;

IConfiguration config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("SEEDER_")
    .AddCommandLine(args)
    .Build();

SeederOptions options = config.Get<SeederOptions>() ?? new SeederOptions();

Console.WriteLine($"Confluence : {options.Confluence.BaseUrl}");
Console.WriteLine($"Seed       : {options.RandomSeed}");

var client = new ConfluenceClient(options.Confluence);

await WarnMissingAuthorsAsync(client);

var corpus = new Corpus(options);
IReadOnlyList<SeedSpace> spaces = corpus.Build();
Console.WriteLine($"Plan       : {spaces.Count} spaces, {CountPages(spaces)} pages/blogs");

await new SeedRunner(client, options).RunAsync(spaces);

if (options.BackdateViaPostgres)
{
    Console.WriteLine("\n=== Postgres backdate pass ===");
    await new PostgresBackdater(options, spaces.Select(s => s.Key).ToArray()).BackdateAsync();
}

return 0;

async Task WarnMissingAuthorsAsync(ConfluenceClient c)
{
    var missing = new List<string>();
    foreach (Author a in Corpus.Authors)
    {
        if (!await c.UserExistsAsync(a.Username))
        {
            missing.Add(a.Username);
        }
    }

    if (missing.Count > 0)
    {
        Console.WriteLine($"WARN: missing author users [{string.Join(", ", missing)}] — those pages fall back to admin.");
        Console.WriteLine("      Create them once (admin must be WebSudo'd), e.g. via the browser snippet in README.");
    }
}

static int CountPages(IReadOnlyList<SeedSpace> spaces)
{
    int n = 0;

    foreach (SeedSpace s in spaces)
    {
        foreach (SeedPage p in s.Pages)
        {
            Walk(p);
        }
    }

    return n;

    void Walk(SeedPage p)
    {
        n++;
        foreach (SeedPage c in p.Children)
        {
            Walk(c);
        }
    }
}
