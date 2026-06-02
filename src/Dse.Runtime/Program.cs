// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Auth;
using Dse.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

namespace Dse.Runtime;

#pragma warning disable S1118 // Class instance needed for tests
internal sealed class Program
#pragma warning restore S1118
{
    public static async Task Main(string[] args)
    {
        try
        {
            await MainAsync(args).ConfigureAwait(false);
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

    private static async Task MainAsync(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        builder.AddServiceDefaults();

        // builder.Services.AddSourceModule<ConfluenceModule>();
        // builder.Services.AddHostedService<SourcesValidator>();

        builder.Services.AddLdapAd();
        builder.Services.AddLdapOud();

        builder.Services
            .AddAuthentication(builder.Environment.IsDevelopment() ? "DevAuth" : PingAuthDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>("DevAuth", null)
            .AddPingAuthentication();

        builder.Services.AddAuthorizationBuilder()
            .SetDefaultPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        builder.Services
            .AddEndpointsApiExplorer()
            .AddSwaggerGen(options => options.SwaggerDoc("v1", new OpenApiInfo { Title = "DSE", Version = "v1" }));

        WebApplication app = builder.Build();

        if (app.Environment.IsProduction())
        {
            app.UseHsts();
        }

        // Skip HTTPS redirect in-container: OpenShift Routes do edge TLS termination,
        // so the pod listens plain HTTP on 8080. A redirect here would 307 every probe.

        app.UseSwagger();
        app.UseSwaggerUI();

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseOutputCache();

        RouteGroupBuilder api = app.MapGroup("api");
        api.MapDseHealthChecks();

        app.UseAuthentication();
        app.UseMiddleware<LdapClaimsEnrichmentMiddleware>();
        app.UseAuthorization();

        api.MapEndpoints();
        await app.RunAsync().ConfigureAwait(false);
    }
}
