// Copyright (c) PNC Financial Services. All rights reserved.


using Microsoft.EntityFrameworkCore;

namespace Dse.Data;

public sealed class IngestRun
{
    public int Id { get; set; }
    public string Name { get; set; } = Guid.NewGuid().ToString();
}

public sealed class DataContext(DbContextOptions<DataContext> options) : DbContext(options)
{
    public DbSet<IngestRun> IngestRuns { get; init; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DataContext).Assembly);
    }
}
