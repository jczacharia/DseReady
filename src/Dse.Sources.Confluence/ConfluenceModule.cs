// Copyright (c) PNC Financial Services. All rights reserved.


using System.Net;
using System.Net.Http.Headers;
using Dse.Shared;
using Dse.Sources;
using Dse.Sources.Confluence;
using Elastic.Mapping;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Timeout;
using Wolverine.Attributes;

[assembly: SourceModule<ConfluenceModule>]
[assembly: WolverineModule]

namespace Dse.Sources.Confluence;

public sealed class ConfluenceModule() : SourceModule("confluence")
{
    /// <summary>
    ///     Retries hard for unattended ingestion.
    /// </summary>
    public const string BackfillClient = "confluence";

    /// <summary>
    ///     Fails fast for user-facing body-view/asset.
    /// </summary>
    public const string ReadThroughClient = "confluence-readthrough";

    /// <summary>
    ///     The AD group DN that gates Confluence search.
    /// </summary>
    public const string Role = "CN=GSGu_CFL_CFLUsers,OU=OUg_Applications,OU=OUc_AccessGroups,DC=pncbank,DC=com";

    public override ElasticsearchTypeContext GetTypeContext(DseEnv dseEnv) =>
        dseEnv is DseEnv.Test
            ? ConfluenceContext.ConfluenceDocTest.CreateContext(Guid.NewGuid().ToString())
            : ConfluenceContext.ConfluenceDoc.Context with { IndexPatternUseBatchDate = true };

    public override void ExtendSearchEndpoint(RouteHandlerBuilder builder) =>
        builder.RequireAuthorization(p => p.RequireRole(Role));

    public override void Build(SourceBuilder builder)
    {
        builder.Services
            .AddFluentOptions<ConfluenceOptions>(ConfluenceOptions.SectionName)
            .PostConfigure<DseEnv>(static (o, env) =>
            {
                o.BaseAddress = o.BaseAddress.Or("https://confluence.pncint.net");

                if (env is { LocalCredentials: { } lc })
                {
                    o.Username = o.Username.Or(lc.Username);
                    o.Password = o.Password.Or(lc.Password);
                }
            });

        builder.AddHealthCheck<ConfluenceHealthCheck>();
        // builder.AddIngestion<ConfluenceDoc, ConfluenceIngest>();

        builder.Services
            .AddHttpClient(BackfillClient, ConfigureClient)
            .ConfigurePrimaryHttpMessageHandler(sp => CreatePrimaryHandler(sp.GetRequiredService<ConfluenceOptions>()))
            .AddResilienceHandler("confluence-backfill", static pipeline =>
            {
                pipeline.AddTimeout(TimeSpan.FromMinutes(2));

                pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 5,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = TimeSpan.FromSeconds(2),
                    MaxDelay = TimeSpan.FromMinutes(2),
                    ShouldHandle = static args => ValueTask.FromResult(
                        args.Outcome.Exception is HttpRequestException or TimeoutRejectedException
                        || args.Outcome.Result is
                        {
                            StatusCode:
                            HttpStatusCode.TooManyRequests or
                            HttpStatusCode.RequestTimeout or
                            HttpStatusCode.BadGateway or
                            HttpStatusCode.ServiceUnavailable or
                            HttpStatusCode.GatewayTimeout,
                        }),
                    DelayGenerator = static args =>
                    {
                        if (args.Outcome.Result?.Headers.RetryAfter is { } ra)
                        {
                            if (ra.Delta is { } d)
                            {
                                return ValueTask.FromResult<TimeSpan?>(d);
                            }

                            if (ra.Date is { } when)
                            {
                                return ValueTask.FromResult<TimeSpan?>(when - DateTimeOffset.UtcNow);
                            }
                        }

                        return ValueTask.FromResult<TimeSpan?>(null);
                    },
                });

                pipeline.AddTimeout(TimeSpan.FromSeconds(30));
            });

        builder.Services.AddHttpClient(ReadThroughClient, ConfigureClient)
            .ConfigurePrimaryHttpMessageHandler(sp => CreatePrimaryHandler(sp.GetRequiredService<ConfluenceOptions>()))
            // Interactive reads: one tight overall budget, no retries — fail fast for the browser/UI. A timeout
            // surfaces as TimeoutRejectedException, which the endpoints map to 504.
            .AddResilienceHandler("confluence-readthrough", static pipeline => pipeline.AddTimeout(TimeSpan.FromSeconds(20)));
    }

    private static void ConfigureClient(IServiceProvider sp, HttpClient http)
    {
        var opts = sp.GetRequiredService<ConfluenceOptions>();
        http.BaseAddress = new Uri(opts.BaseAddress);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        if (opts is { Username: { Length: > 0 } u, Password: { Length: > 0 } p })
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Utils.EncodeBasicAuth(u, p));
        }
    }

    private static SocketsHttpHandler CreatePrimaryHandler(ConfluenceOptions opts) => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        UseProxy = !string.IsNullOrEmpty(opts.Proxy),
        Proxy = opts.Proxy is { Length: > 0 } p && Uri.IsWellFormedUriString(p, UriKind.Absolute) ? new WebProxy(p) : null,
    };
}
