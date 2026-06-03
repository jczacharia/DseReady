// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Data;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace Dse.Ingestion;

public sealed class IngestRunCreatedHandler
{
    public static async IAsyncEnumerable<IngestRunProgressed> Handle(
        IngestRunCreated message,
        DataContext dataContext,
        IServiceProvider services)
    {
        if(await dataContext.IngestRuns.FirstOrDefaultAsync(r => r.Id == message.RunId) is not { } run)
        {
            throw new InvalidOperationException($"Ingest run with ID {message.RunId} not found");
        }



            yield return new IngestRunProgressed(message.RunId);


            await dataContext.SaveChangesAsync();
    }

    // private static async Task ProgressAsync(IngestRunProgress progress)
    // {
    //
    // }
}
