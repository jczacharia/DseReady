// Copyright (c) PNC Financial Services. All rights reserved.


using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dse.Sources;

public sealed class SourcesValidator(IEnumerable<SourceModule> modules, ILogger<SourcesValidator> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            SourceModule[] mods = modules.ToArray();
            Dictionary<SourceKey, int> groups = mods
                .GroupBy(k => k.SourceKey)
                .ToDictionary(g => g.Key, g => g.Count());

            if (groups.Count == 0)
            {
                throw new InvalidOperationException(
                    "No source modules found. At least one source module assembly with a SourceModuleAttribute is required.");
            }

            List<SourceKey> duplicates = groups.Where(g => g.Value > 1).Select(g => g.Key).ToList();
            if (duplicates.Count != 0)
            {
                throw new InvalidOperationException(
                    $"Duplicate source keys found: {string.Join(", ", duplicates)}. Each source module must have a unique key.");
            }

            logger.LogInformation("Validated {SourceModuleCount} source modules with keys: {SourceKeys}",
                groups.Count, string.Join(", ", groups.Keys));

            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
