// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics;
using Dse.Data;
using Dse.Runtime;
using Dse.Shared;
using Dse.Tests;
using Elastic.Clients.Elasticsearch;
using Elastic.Mapping;
using JasperFx.CommandLine;
using JasperFx.Resources;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weasel.EntityFrameworkCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;

[assembly: CaptureConsole(CaptureError = true, CaptureOut = true)]
[assembly: AssemblyFixture(typeof(TestFixture))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Dse.Tests;

public sealed class TestFixture : IAsyncLifetime
{
    private const string ElasticVersion = "8.19.14";
    private const int ElasticPort = 9200;

    private static readonly TimeSpan s_readinessTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_downloadTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan s_shutdownTimeout = TimeSpan.FromSeconds(30);

    private IAlbaHost? _host;

    private Process? _process;
    public IAlbaHost Host => _host ?? throw new InvalidOperationException("Test fixture not initialized.");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dbFilename = $"{Guid.NewGuid().ToString("N")[..10]}.db";

    public async ValueTask InitializeAsync()
    {
        ConfigurationBuilder config = new();

        if (IDseEnvironment.IsRelease)
        {
            string esHome = await EnsureEsDownloadedAsync();
            await StartAsync(esHome);
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Elastic:BaseAddress"] = await StartAsync(esHome),
            });
        }

        JasperFxEnvironment.AutoStartHost = true;
        _host = await AlbaHost.For<Program>(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureLogging(loggingBuilder =>
            {
                loggingBuilder.ClearProviders();
                loggingBuilder.AddProvider(new TestContextLoggerProvider());
            });

            builder.ConfigureAppConfiguration(sources =>
            {
                for (int i = sources.Sources.Count - 1; i >= 0; i--)
                {
                    if (sources.Sources[i] is EnvironmentVariablesConfigurationSource)
                    {
                        sources.Sources.RemoveAt(i);
                    }
                }
            });

            builder.ConfigureServices(services =>
            {
                services.AddDatabaseCleaner<DataContext>();
                services.AddInitialData<DataContext, DataMigrator>();
                services.RunWolverineInSoloMode();
                services
                    .AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", configureOptions: null);
            });
        }, new TestConfigurationExtension(builder =>
        {
            builder.AddUserSecrets("dse");
            builder.AddConfiguration(config.Build());
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = $"Data Source={_dbFilename}",
            });
        }));

        _host.BeforeEach(context =>
        {
            context.Request.EnableBuffering();
        });

        await TearDownAsync(_host.Services);
    }

    public async Task TearDownAsync(IServiceProvider services)
    {
        await Host.ResetResourceState(Ct);

        // No deleting 'test-*' wildcard as in local development (not CI/Release) as ES is a shared resource.
        // We don't add it add all so behavior is the same.

        var esClient = services.GetRequiredService<ElasticsearchClient>();
        foreach (ElasticsearchTypeContext typeContext in services.GetServices<ElasticsearchTypeContext>())
        {
            string index = typeContext.ResolveIndexFormat();
            Assert.StartsWith("test-", index);
            await Utils.IgnoreException(() => esClient.Indices.DeleteIndexTemplateAsync($"{index}-template", Ct));
            await Utils.IgnoreException(() => esClient.Cluster.DeleteComponentTemplateAsync($"{index}-template-mappings", Ct));
            await Utils.IgnoreException(() => esClient.Cluster.DeleteComponentTemplateAsync($"{index}-template-settings", Ct));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await (_host?.StopAsync(Ct) ?? Task.CompletedTask);
        await (_host?.DisposeAsync() ?? ValueTask.CompletedTask);

        // Clear the SQLite connection pool so the file handle is released,
        // then delete the per-run database file.
        SqliteConnection.ClearAllPools();
        if (!string.IsNullOrEmpty(_dbFilename) && File.Exists(_dbFilename))
        {
            File.Delete(_dbFilename);
        }

        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
                await _process.WaitForExitAsync(Ct).WaitAsync(s_shutdownTimeout, Ct);
            }
        }
        catch
        {
            // Best-effort; OS will reap on test-host exit.
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private static async Task<string> EnsureEsDownloadedAsync()
    {
        string version = Environment.GetEnvironmentVariable("ES_VERSION") is { Length: > 0 } v ? v : ElasticVersion;
        string cacheRoot = Path.Combine(Path.GetTempPath(), "dse-test-es-cache");
        Directory.CreateDirectory(cacheRoot);

        string extractedHome = Path.Combine(cacheRoot, $"elasticsearch-{version}");
        if (File.Exists(Path.Combine(extractedHome, "bin", "elasticsearch")))
        {
            return extractedHome;
        }

        string tarballPath = Path.Combine(cacheRoot, $"elasticsearch-{version}-linux-x86_64.tar.gz");
        if (!File.Exists(tarballPath))
        {
            string url = Environment.GetEnvironmentVariable("ES_TARBALL_URL") is { Length: > 0 } u
                ? u
                : $"https://artifacts.elastic.co/downloads/elasticsearch/elasticsearch-{version}-linux-x86_64.tar.gz";
            await DownloadAsync(url, tarballPath);
        }

        await ExtractAsync(tarballPath, cacheRoot);
        if (!File.Exists(Path.Combine(extractedHome, "bin", "elasticsearch")))
        {
            throw new InvalidOperationException(
                $"Extracted tarball did not produce expected layout at {extractedHome}.");
        }

        return extractedHome;
    }

    private static async Task DownloadAsync(string url, string destination)
    {
        string? proxy = Environment.GetEnvironmentVariable("HTTPS_PROXY")
                        ?? Environment.GetEnvironmentVariable("https_proxy")
                        ?? Environment.GetEnvironmentVariable("AGENT_PROXYURL");

        HttpClientHandler handler = new();
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        using HttpClient http = new(handler) { Timeout = s_downloadTimeout };
        await using FileStream fs = File.Create(destination);
        using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await response.Content.CopyToAsync(fs);
    }

    private static async Task ExtractAsync(string tarball, string destinationDir)
    {
        using Process tar = new();
        tar.StartInfo = new ProcessStartInfo("tar", $"-xzf \"{tarball}\" -C \"{destinationDir}\"")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        tar.Start();
        await tar.WaitForExitAsync();
        if (tar.ExitCode != 0)
        {
            string err = await tar.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"tar -xzf failed (exit {tar.ExitCode}): {err}");
        }
    }

    private async Task<string> StartAsync(string esHome)
    {
        string esBin = Path.Combine(esHome, "bin", "elasticsearch");
        ProcessStartInfo psi = new()
        {
            FileName = esBin,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = esHome,
        };
        // Single-node, no security, no ML/geoip, mmap off so we don't need vm.max_map_count, modest heap.
        psi.ArgumentList.Add("-E");
        psi.ArgumentList.Add("discovery.type=single-node");
        psi.ArgumentList.Add("-E");
        psi.ArgumentList.Add("xpack.security.enabled=false");
        psi.ArgumentList.Add("-E");
        psi.ArgumentList.Add("xpack.ml.enabled=false");
        psi.ArgumentList.Add("-E");
        psi.ArgumentList.Add("ingest.geoip.downloader.enabled=false");
        psi.ArgumentList.Add("-E");
        psi.ArgumentList.Add("node.store.allow_mmap=false");
        psi.ArgumentList.Add("-E");
        psi.ArgumentList.Add("network.host=127.0.0.1");
        psi.ArgumentList.Add("-E");
        psi.ArgumentList.Add($"http.port={ElasticPort}");
        psi.Environment["ES_JAVA_OPTS"] = Environment.GetEnvironmentVariable("ES_JAVA_OPTS") ?? "-Xms512m -Xmx512m";
        // Headless ES install needs JAVA_HOME or its own bundled JDK. PNC builder image ships JAVA_HOME pointing
        // at openjdk-21. ES 8.19 bundles its own JDK in $ES_HOME/jdk, so this is belt-and-suspenders.
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } javaHome)
        {
            psi.Environment["ES_JAVA_HOME"] = javaHome;
        }

        _process = Process.Start(psi)
                   ?? throw new InvalidOperationException($"Failed to start Elasticsearch from {esBin}");

        // Drain stdout/stderr so the pipes don't fill and block the child. Output is intentionally swallowed —
        // when something fails the readiness probe surfaces it; if you need to debug, redirect to a file.
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardOutput.ReadLineAsync(Ct) is not null)
                {
                    /* drain */
                }
            }
            catch
            {
                /* process exited */
            }
        }, Ct);
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync(Ct) is not null)
                {
                    /* drain */
                }
            }
            catch
            {
                /* process exited */
            }
        }, Ct);

        string url = $"http://127.0.0.1:{ElasticPort}";
        await WaitForReadyAsync(url);
        return url;
    }

    private static async Task WaitForReadyAsync(string baseAddress)
    {
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
        var sw = Stopwatch.StartNew();
        Exception? last = null;
        while (sw.Elapsed < s_readinessTimeout)
        {
            try
            {
                using HttpResponseMessage response =
                    await http.GetAsync($"{baseAddress}/_cluster/health?wait_for_status=yellow&timeout=5s");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException(
            $"Elasticsearch at {baseAddress} did not become ready within {s_readinessTimeout.TotalSeconds:N0}s.",
            last);
    }
}
