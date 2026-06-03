// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Auth;
using Dse.Shared;
using Dse.Sources;
using Dse.Sources.Confluence;
using JasperFx;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
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

        builder.Services.AddSourceModule<ConfluenceModule>();

        builder.Services.AddEndpoints([typeof(Program).Assembly]);
        builder.Services.AddSignalR(e =>
        {
            if (!builder.Environment.IsProduction())
            {
                e.EnableDetailedErrors = true;
            }
        });

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

        // Apache proxies "/adv/api/*" to "searchapi/api/*" — we receive "/api/*" with no idea about the
        // "/adv" prefix. Trust the X-Forwarded-* headers apache sends so PathBase reflects the EXTERNAL
        // base ("/adv/api"), which makes LinkGenerator emit correct Location headers and lets the Swagger
        // PreSerializeFilter advertise the real servers URL to browsers.
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto
                                       | ForwardedHeaders.XForwardedHost
                                       | ForwardedHeaders.XForwardedPrefix;
            // Inside OpenShift the proxy IP is dynamic and not in the default KnownNetworks list, so the
            // forwarded headers would be dropped silently. Clear the allow-list to trust whatever forwards
            // to us — the pod is only reachable via the cluster Route, which is the trust boundary.
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
        });

        WebApplication app = builder.Build();

        // MUST run before anything that inspects Scheme/Host/PathBase (UsePathBase, LinkGenerator,
        // Swashbuckle). Translates apache's X-Forwarded-* into the request's canonical fields.
        app.UseForwardedHeaders();

        // UsePathBase is non-strict: it strips "/api" if present but does NOT reject requests that
        // lack the prefix, which lets /swagger/index.html alias /api/swagger/index.html. Gate the
        // pipeline to "/api/*" before UsePathBase so the strip-then-route only ever sees /api requests.
        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next();
        });
        app.UsePathBase("/api");

        // Exception handling must come early so it can catch failures from every middleware below it —
        // HTTPS redirect, Swagger, auth, the LDAP enrichment middleware, and the routed endpoints.
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
        app.UseSwagger(o =>
        {
            o.RouteTemplate = "swagger/{documentName}/swagger.json";

            // Without this, the OpenAPI doc has no `servers` entry; Swagger UI then resolves operation
            // paths against the host root and fires "Try it out" requests at /api/... — missing the
            // /adv prefix the reverse proxy expects, producing 404s. PathBase here is fed by
            // X-Forwarded-Prefix (apache must `RequestHeader set X-Forwarded-Prefix "/adv"`), so the
            // emitted server URL is the real external base in every environment.
            o.PreSerializeFilters.Add((document, httpReq) =>
                document.Servers =
                [
                    new OpenApiServer { Url = $"{httpReq.Scheme}://{httpReq.Host.Value}{httpReq.PathBase}" },
                ]);
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

        app.MapDefaultEndpoints().AllowAnonymous();

        app.UseAuthentication();
        app.UseMiddleware<LdapClaimsEnrichmentMiddleware>();
        app.UseAuthorization();

        app.MapEndpoints().RequireAuthorization();
        return await app.RunJasperFxCommands(args);
    }
}
