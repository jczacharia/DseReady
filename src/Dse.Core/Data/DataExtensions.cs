// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Shared;
using Gridify;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Thinktecture;
using Wolverine.EntityFrameworkCore;

namespace Dse.Data;

public static class DataExtensions
{
    public static string GetSqliteConnectionString(this IConfiguration configuration) =>
        configuration.GetConnectionString("sqlite").Or("Data Source=dse.db");

    public static string GetSqliteConnectionString(this IServiceProvider services) =>
        services.GetRequiredService<IConfiguration>().GetSqliteConnectionString();

    public static void AddDataContext(this IHostApplicationBuilder builder)
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
            dbCtxOpts.UseThinktectureValueConverters();
        });
    }
}
