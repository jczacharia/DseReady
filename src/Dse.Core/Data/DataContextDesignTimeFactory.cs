// Copyright (c) PNC Financial Services. All rights reserved.


using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Thinktecture;

namespace Dse.Data;

/// <summary>
///     Builds a <see cref="DataContext" /> for the EF Core design-time tools (<c>dotnet ef migrations</c>) without
///     standing up the application host or Wolverine. The connection string is irrelevant for scaffolding, but the
///     model-affecting options must mirror <c>DataExtensions.AddDataContext</c> so generated migrations match the
///     runtime model.
/// </summary>
public sealed class DataContextDesignTimeFactory : IDesignTimeDbContextFactory<DataContext>
{
    public DataContext CreateDbContext(string[] args)
    {
        DbContextOptions<DataContext> options = new DbContextOptionsBuilder<DataContext>()
            .UseSqlite("Data Source=dse-designtime.db")
            .UseProjectables()
            .UseThinktectureValueConverters()
            .Options;

        return new DataContext(options);
    }
}
