// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Ingestion;
using Dse.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Thinktecture;

namespace Dse.Data;

/// <summary>
///     Stores a <see cref="DateTimeOffset" /> as its UTC tick count. SQLite's TEXT representation can't be ordered
///     or compared server-side (EF throws on ORDER BY), and a monotonic <c>long</c> sorts chronologically. Every
///     timestamp in this model is UTC, so collapsing the (always-zero) offset loses nothing.
/// </summary>
public sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
    offset => offset.UtcTicks,
    ticks => new DateTimeOffset(ticks, TimeSpan.Zero));

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

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddThinktectureValueConverters();
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DataContext).Assembly);

        // Every aggregate assigns its own key in the domain (a run's id is published on its creation event and
        // returned in the Location header before it is ever saved). Tell EF the keys are application-assigned,
        // otherwise a non-empty Guid on a child added to an already-tracked parent is mistaken for an existing
        // row and EF emits an UPDATE-that-matches-nothing instead of an INSERT.
        foreach (IMutableProperty keyProperty in modelBuilder.Model.GetEntityTypes()
                     .SelectMany(entityType => entityType.GetKeys())
                     .SelectMany(key => key.Properties))
        {
            keyProperty.ValueGenerated = ValueGenerated.Never;
        }
    }
}
