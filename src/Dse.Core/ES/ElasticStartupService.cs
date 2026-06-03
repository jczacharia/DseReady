// Copyright (c) PNC Financial Services. All rights reserved.


using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dse.ES;

public sealed record ElasticStartupData(
    int MaxChannelConcurrency,
    long BulkMaxByteSize,
    int DataNodeCount);

public sealed class ElasticStartupService(
    ILogger<ElasticStartupService> logger,
    ElasticsearchClient client,
    DseEnv env,
    IConfiguration cfg) : BackgroundService
{
    private const long DefaultBulkMaxByteSize = 100L * 1024 * 1024;

    private static readonly ElasticStartupData s_fallbackData =
        new(Math.Max(val1: 2, Environment.ProcessorCount), DefaultBulkMaxByteSize, DataNodeCount: 0);

    private volatile ElasticStartupData _data = s_fallbackData;

    public ElasticStartupData Data => _data;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (env is DseEnv.Test)
        {
            _data = new ElasticStartupData(MaxChannelConcurrency: 15, DefaultBulkMaxByteSize, DataNodeCount: 2);
            return;
        }

        logger.LogInformation("Elasticsearch starting...");

        try
        {
            _data = await ProbeClusterAsync(stoppingToken);
            logger.LogInformation("Elasticsearch started successfully");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Elasticsearch startup probe failed; continuing with fallback sizing {@FallbackData}. Ingestion will not function until the cluster is reachable",
                s_fallbackData);
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

        double nodeUtilization = cfg.GetValue<double?>("DSE_NODE_UTILIZATION") ?? 0.75;
        double clientOversubscription = cfg.GetValue<double?>("DSE_CLIENT_OVERSUBSCRIPTION") ?? 2.0;

        int clusterCeiling = Math.Max(val1: 2, (int)(writePoolCapacity * nodeUtilization));
        int clientCeiling = Math.Max(val1: 2, (int)(Environment.ProcessorCount * clientOversubscription));

        int maxChannelConcurrency = Math.Min(clusterCeiling, clientCeiling);

        logger.LogInformation(
            "Cluster sizing: dataNodes={DataNodes}, writePoolCapacity={WritePool}, "
            + "clusterCeiling={ClusterCeiling} (×{NodeUtilization}), "
            + "clientCeiling={ClientCeiling} (×{ClientOversubscription}, cores={Cores}), "
            + "→ maxChannelConcurrency={MaxChannelConcurrency}, bulkMaxByteSize={BulkMaxByteSize:N0}",
            dataNodeCount, writePoolCapacity,
            clusterCeiling, nodeUtilization,
            clientCeiling, clientOversubscription, Environment.ProcessorCount,
            maxChannelConcurrency, bulkMaxByteSize);

        return new ElasticStartupData(maxChannelConcurrency, bulkMaxByteSize, dataNodeCount);
    }
}
