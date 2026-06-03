// Copyright (c) PNC Financial Services. All rights reserved.


using Gridify;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.EntityFrameworkCore;

namespace Dse.Data;

public static class DataExtensions
{
    public static string GetSqliteConnectionString(this IConfiguration configuration) =>
        configuration.GetConnectionString("sqlite")
        ?? throw new InvalidOperationException("Connection string 'sqlite' not found.");

    public static string GetSqliteConnectionString(this IServiceProvider services) =>
        services.GetRequiredService<IConfiguration>().GetSqliteConnectionString();

    public static void AddData(this IHostApplicationBuilder builder)
    {
        GridifyGlobalConfiguration.EnableEntityFrameworkCompatibilityLayer();

        builder.Services.AddDbContextWithWolverineIntegration<DataContext>((sp, dbCtxOpts) =>
        {
            if (!builder.Environment.IsProduction())
            {
                dbCtxOpts.EnableSensitiveDataLogging();
                dbCtxOpts.EnableDetailedErrors();
            }

            dbCtxOpts.UseSqlite(sp.GetSqliteConnectionString());
            dbCtxOpts.UseProjectables();
        });
    }
}
