// Copyright (c) PNC Financial Services. All rights reserved.


namespace Dse.Confluence.Seeder;

public sealed class SeedRunner(ConfluenceClient client, SeederOptions options)
{
    private int _attachments;
    private int _created;
    private int _skipped;
    private int _versions;

    public async Task RunAsync(IReadOnlyList<SeedSpace> spaces)
    {
        foreach (SeedSpace space in spaces)
        {
            Console.WriteLine($"\n=== Space {space.Key} ({space.Name}) ===");
            await client.EnsureSpaceAsync(space);
            foreach (SeedPage root in space.Pages)
            {
                await CreateTreeAsync(space.Key, root, parentId: null);
            }
        }

        Console.WriteLine(
            $"\nDone. created={_created} skipped(existing)={_skipped} versions={_versions} attachments={_attachments}");
    }

    private async Task CreateTreeAsync(string spaceKey, SeedPage page, string? parentId)
    {
        string? existing = await client.FindContentIdAsync(spaceKey, page.Title, page.Type);
        if (existing is not null)
        {
            page.Id = existing;
            Interlocked.Increment(ref _skipped);
        }
        else
        {
            HttpClient author = await client.ForAuthorAsync(page.Author);
            page.Id = await ConfluenceClient.CreateContentAsync(spaceKey, page, parentId, author);
            Interlocked.Increment(ref _created);

            await client.AddLabelsAsync(page.Id, page.Labels);

            if (options.Corpus.UploadAttachments)
            {
                foreach (SeedAttachment att in page.Attachments)
                {
                    await client.UploadAttachmentAsync(page.Id, att);
                    Interlocked.Increment(ref _attachments);
                }
            }

            foreach ((string body, Author? versionAuthor) in page.ExtraVersions)
            {
                HttpClient vClient = await client.ForAuthorAsync(versionAuthor);
                await client.UpdateContentAsync(page.Id, page.Title, page.Type, body, vClient);
                Interlocked.Increment(ref _versions);
            }

            Console.WriteLine($"  + {page.Type,-8} {page.Title}");
        }

        if (page.Children.Count == 0)
        {
            return;
        }

        var opts = new ParallelOptions { MaxDegreeOfParallelism = options.Confluence.MaxConcurrency };
        await Parallel.ForEachAsync(page.Children, opts,
            async (child, _) => await CreateTreeAsync(spaceKey, child, page.Id));
    }
}
