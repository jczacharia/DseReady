// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Shared;
using Elastic.Channels;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnitsNet;

namespace Dse.ES;

public sealed record ElasticStartupData(
    int MaxChannelConcurrency,
    long BulkMaxByteSize,
    int DataNodeCount);

public sealed class ElasticStartupService(
    ILogger<ElasticStartupService> logger,
    ElasticsearchClient client,
    IOptions<ElasticOptions> options,
    ElasticChangeTokenSource<BufferOptions> changeTokenSource) : BackgroundService
{
    private const long DefaultBulkMaxByteSize = 100L * 1024 * 1024;

    // Until the cluster probe completes, fall back to the configured export cap. Ingestion stays inert until the
    // probe succeeds, so this only governs the brief startup window.
    private volatile ElasticStartupData _data =
        new(Math.Max(val1: 2, options.Value.MaxExportConcurrency), DefaultBulkMaxByteSize, DataNodeCount: 0);

    public ElasticStartupData Data => _data;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Elasticsearch starting...");

        try
        {
            _data = await ProbeClusterAsync(stoppingToken);
            changeTokenSource.TriggerReload();
            logger.LogInformation("Elasticsearch started successfully");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Elasticsearch startup probe failed; continuing with fallback sizing {@FallbackData}."
                + " Ingestion will not function until the cluster is reachable", _data);
        }
    }

    private async Task<ElasticStartupData> ProbeClusterAsync(CancellationToken ct)
    {
        NodesInfoResponse response = await client.Nodes.InfoAsync(nodeId: null, Metrics.All, ct);
        if (!response.IsValidResponse)
        {
            throw new InvalidOperationException($"Nodes info failed: {response.DebugInformation}");
        }

        int dataNodeCount = 0;
        int writePoolCapacity = 0;
        long bulkMaxByteSize = long.MaxValue;

        foreach ((_, NodeInfo node) in response.Nodes)
        {
            bool isDataNode = node.Roles.Any(r => r.ToString().StartsWith("data", StringComparison.OrdinalIgnoreCase));

            if (!isDataNode)
            {
                continue;
            }

            dataNodeCount++;

            if (node.ThreadPool is { } pools
                && pools.TryGetValue("write", out NodeThreadPoolInfo? write)
                && write.Size is { } size)
            {
                writePoolCapacity += size;
            }

            if (node.Http?.MaxContentLengthInBytes is { } maxBytes && maxBytes < bulkMaxByteSize)
            {
                bulkMaxByteSize = maxBytes;
            }
        }

        if (writePoolCapacity <= 0)
        {
            throw new InvalidOperationException(
                $"No data nodes reported a 'write' thread pool size: {response.DebugInformation}");
        }

        if (bulkMaxByteSize == long.MaxValue)
        {
            bulkMaxByteSize = DefaultBulkMaxByteSize;
        }

        // The write pool caps what the cluster can absorb; MaxExportConcurrency caps what a single (possibly
        // single-core) client can actually drive. The smaller wins. Core count is deliberately not a factor —
        // this export path is I/O-bound, not CPU-bound.
        int clusterCeiling = Math.Max(val1: 2, (int)(writePoolCapacity * options.Value.NodeUtilization));
        int maxChannelConcurrency = Math.Min(clusterCeiling, Math.Max(val1: 2, options.Value.MaxExportConcurrency));

        logger.LogInformation("Cluster sizing: {@ClusterSizing}", new
        {
            dataNodeCount,
            writePoolCapacity,
            clusterCeiling,
            options.Value.NodeUtilization,
            options.Value.MaxExportConcurrency,
            maxChannelConcurrency,
            bulkMaxByteSize = Information.FromBytes(bulkMaxByteSize).Humanize(),
        });

        return new ElasticStartupData(maxChannelConcurrency, bulkMaxByteSize, dataNodeCount);
    }
}
