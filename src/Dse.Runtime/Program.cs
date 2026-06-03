// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Auth;
using Dse.Shared;
using Dse.Sources;
using Dse.Sources.Confluence;
using JasperFx;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
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

        builder.Services.AddSourceModule<ConfluenceModule>();
        builder.Services.AddHostedService<SourcesValidator>();

        if (builder.Environment.EnvironmentName != "Test")
        {
            builder.Services.AddLdapAd();
            builder.Services.AddLdapOud();

            builder.Services
                .AddAuthentication(builder.Environment.IsDevelopment() ? "DevAuth" : PingAuthDefaults.AuthenticationScheme)
                .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>("DevAuth", null)
                .AddPingAuthentication();
        }

        builder.Services
            .AddAuthorizationBuilder()
            .SetDefaultPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        WebApplication app = builder.Build();

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

        RouteGroupBuilder api = app.MapGroup("");

        app.UseSwagger();
        app.UseStaticFiles();
        app.UseSwaggerUI(c =>
        {
            c.DocumentTitle = "DSE OpenAPI | Enterprise Search";
            c.SwaggerEndpoint("/swagger.json", "v1");
            c.RoutePrefix = string.Empty;
            c.EnableDeepLinking();
            c.DisplayOperationId();
        });

        app.UseExceptionHandler();
        app.UseStatusCodePages();

        api.MapDefaultEndpoints().AllowAnonymous();

        app.UseAuthentication();
        app.UseMiddleware<LdapClaimsEnrichmentMiddleware>();
        app.UseAuthorization();

        api.MapEndpoints().RequireAuthorization();
        return await app.RunJasperFxCommands(args);
    }
}
