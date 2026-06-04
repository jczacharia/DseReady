// Copyright (c) PNC Financial Services. All rights reserved.


using Microsoft.Data.Sqlite;
using Respawn;
using Respawn.Graph;

namespace Dse.Tests;

/// <summary>
///     Resets the mutable ingest data between tests. Schema and reference data are owned by EF Core migrations and
///     the source seed (applied once at host startup), so this only deletes from the two tables tests write to —
///     <c>IngestRun</c> and its cascaded <c>IngestProgress</c>. The migrations-history, <c>Source</c>, and Wolverine
///     envelope tables are deliberately left untouched (Wolverine state is reset separately via ResetResourceState).
/// </summary>
public static class DataContextRespawner
{
    private static readonly SemaphoreSlim s_gate = new(initialCount: 1, maxCount: 1);
    private static Respawner? s_respawner;

    public static async Task RespawnAsync(string connectionString, CancellationToken ct = default)
    {
        await s_gate.WaitAsync(ct);
        try
        {
            await using SqliteConnection connection = new(connectionString);
            await connection.OpenAsync(ct);

            s_respawner ??= await Respawner.CreateAsync(connection, new RespawnerOptions
            {
                DbAdapter = DbAdapter.Sqlite,
                TablesToInclude = [new Table("IngestRun"), new Table("IngestProgress")],
            });

            await s_respawner.ResetAsync(connection);
        }
        finally
        {
            s_gate.Release();
        }
    }
}
