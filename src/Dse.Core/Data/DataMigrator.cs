// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using Dse.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dse.Data;

/// <summary>
///     Brings the database schema up to date with EF Core migrations and seeds the source registry on startup.
///     A single always-on pod with a disposable SQLite database makes applying migrations at startup the right
///     trade-off here (no farm, no concurrent migrators); EF's own migration lock guards the rest.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class DataMigrator(IEnumerable<SourceModule> modules, IServiceProvider sp) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = sp.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<DataContext>();

        await context.Database.MigrateAsync(cancellationToken);
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
