// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Auth;
using Dse.Ingestion.Endpoints;
using Dse.Shared;
using Dse.Sources;
using Dse.Sources.Confluence;
using JasperFx;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Thinktecture.Swashbuckle;

namespace Dse.Runtime;

#pragma warning disable S1118 // Class instance needed for tests
internal sealed class Program
#pragma warning restore S1118
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await MainCore(args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            //OpenShift/Helm last-resort visibility
            // ValidateOnBuild and other startup failures throw
            // before the logging pipeline has a chance to flush. Write straight to stderr
            // so OpenShift `oc logs` shows the real cause instead of a silent exit.
            await Console.Error.WriteLineAsync($"FATAL STARTUP: {ex}").ConfigureAwait(false);
            await Console.Error.FlushAsync().ConfigureAwait(false);
            await Console.Out.FlushAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<int> MainCore(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();

        builder.Services.AddSourceManifest<Confluence>();

        builder.Services
            .AddEndpointsApiExplorer()
            .AddSwaggerGen(options =>
            {
                foreach (string xmlPath in Directory.EnumerateFiles(AppContext.BaseDirectory, "Dse.*.xml"))
                {
                    options.IncludeXmlComments(xmlPath);
                }

                options.SwaggerDoc("v1", new OpenApiInfo
                {
                    Version = "v1",
                    Title = "DSE",
                    Description = "Enterprise Search",
                });
            })
            .AddThinktectureOpenApiFilters();

        builder.Services.AddSignalR(e =>
        {
            if (!builder.Environment.IsProduction())
            {
                e.EnableDetailedErrors = true;
            }
        });

        if (builder.Environment.EnvironmentName != "Test")
        {
            builder.Services.AddLdapAd();
            builder.Services.AddLdapOud();

            builder.Services
                .AddAuthentication(builder.Environment.IsDevelopment() ? "DevAuth" : PingAuthDefaults.AuthenticationScheme)
                .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>("DevAuth", configureOptions: null)
                .AddPingAuthentication();
        }

        builder.Services
            .AddAuthorizationBuilder()
            .SetDefaultPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        // Trust apache's X-Forwarded-* so PathBase reflects "/adv/api" (X-Forwarded-Prefix + UsePathBase).
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto
                                       | ForwardedHeaders.XForwardedHost
                                       | ForwardedHeaders.XForwardedPrefix;
            // OpenShift proxy IP is dynamic; cluster Route is the trust boundary.
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
        });

        WebApplication app = builder.Build();

        // Must run before anything that reads Scheme/Host/PathBase.
        app.UseForwardedHeaders();
        app.UsePathBase("/api");

        app.UseExceptionHandler();
        app.UseStatusCodePages();

        if (app.Environment.IsProduction())
        {
            app.UseHsts();
        }

        // Skip HTTPS redirect in-container: OpenShift Routes do edge TLS termination,
        // so the pod listens plain HTTP on 8080. A redirect here would 307 every probe.
        if (!builder.Environment.IsProduction())
        {
            app.UseHttpsRedirection();
        }

        app.UseStaticFiles();

        // Pinned servers URL override (env OpenApi__ExternalBaseUrl) — safety net if apache's
        // X-Forwarded-Prefix isn't set yet.
        string? configuredExternalBase = app.Configuration["OpenApi:ExternalBaseUrl"]?.TrimEnd('/');

        app.UseSwagger(o =>
        {
            o.RouteTemplate = "swagger/{documentName}/swagger.json";

            // Without an explicit servers entry, "Try it out" fires at /api/... missing the /adv prefix.
            o.PreSerializeFilters.Add((document, httpReq) =>
            {
                string url = configuredExternalBase
                             ?? $"{httpReq.Scheme}://{httpReq.Host.Value}{httpReq.PathBase}";
                document.Servers = [new OpenApiServer { Url = url }];
            });
        });
        app.UseSwaggerUI(c =>
        {
            c.DocumentTitle = "DSE OpenAPI | Enterprise Search";
            c.SwaggerEndpoint("v1/swagger.json", "v1");
            c.RoutePrefix = "swagger";
            c.EnableDeepLinking();
            c.DisplayOperationId();
        });
        app.MapSwagger();

        app.MapDefaultHealthChecks().AllowAnonymous();

        app.UseAuthentication();

        // In development DevAuth supplies entitlements directly; there is no directory to enrich against, so skip
        // LDAP enrichment (it would otherwise fail to connect and fault every request).
        if (!app.Environment.IsDevelopment())
        {
            app.UseMiddleware<LdapClaimsEnrichmentMiddleware>();
        }

        app.UseAuthorization();

        RouteGroupBuilder sources = app.MapGroup("sources").RequireAuthorization();

        foreach (var module in app.Services.GetServices<SourceModule>())
        {
            module.Configure(sources);
        }

        // Cross-source ops board — deliberately not confined under /sources/{key}.
        app.MapIngestionOverviewEndpoint();

        return await app.RunJasperFxCommands(args);
    }
}
