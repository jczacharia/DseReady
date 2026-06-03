// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Ingestion;
using Dse.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Thinktecture;

namespace Dse.Data;

public sealed class DataContext(DbContextOptions<DataContext> options) : DbContext(options)
{
    public DbSet<Source> Sources { get; init; }
    public DbSet<IngestRun> IngestRuns { get; init; }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        HandleEntities();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        HandleEntities();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    private void HandleEntities()
    {
        foreach (EntityEntry<IEntity> entry in ChangeTracker.Entries<IEntity>().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = DateTimeOffset.UtcNow;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = DateTimeOffset.UtcNow;
                    break;
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddThinktectureValueConverters();
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DataContext).Assembly);
    }
}
