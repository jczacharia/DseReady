// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weasel.EntityFrameworkCore;

namespace Dse.Sources;

public sealed class SourceDataInitializer(IEnumerable<SourceModule> modules) : IInitialData<DataContext>
{
    public async Task Populate(DataContext context, CancellationToken cancellation)
    {
        foreach (SourceModule module in modules)
        {
            if (await context.Sources.FindAsync([module.SourceKey], cancellation) is null)
            {
                context.Sources.Add(Source.FromModule(module));
            }
        }

        await context.SaveChangesAsync(cancellation);
    }
}
