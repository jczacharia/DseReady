// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Sources;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weasel.Core;
using Weasel.EntityFrameworkCore;

namespace Dse.Data;

public sealed class DataMigrator(IEnumerable<SourceModule> modules, IServiceProvider sp)
    : IInitialData<DataContext>, IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = sp.CreateAsyncScope();
        await using var context = scope.ServiceProvider.GetRequiredService<DataContext>();
        await using var migration = await sp.CreateMigrationAsync(context, cancellationToken);

        if (migration.Migration.Difference is not SchemaPatchDifference.None)
        {
            await migration.ExecuteAsync(AutoCreate.All, cancellationToken);
        }

        await Populate(context, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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
