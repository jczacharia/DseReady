// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Dse.Auth;

[ExcludeFromCodeCoverage]
public static class PingAuthDefaults
{
    public const string AuthenticationScheme = "Ping";
}

[ExcludeFromCodeCoverage]
internal class ConfigurePingJwtBearerOptions(DseEnvironment env) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != PingAuthDefaults.AuthenticationScheme)
        {
            return;
        }

        string metadataAddress = env switch
        {
            DseEnvironment.Rnd => "https://wfsso-apps-rnd.pnc.com/.well-known/openid-configuration",
            DseEnvironment.Uat => "https://wfsso-apps-uat.pnc.com/.well-known/openid-configuration",
            DseEnvironment.Qa => "https://wfsso-apps-qa.pnc.com/.well-known/openid-configuration",
            _ => "https://wfsso-apps.pnc.com/.well-known/openid-configuration",
        };

        options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true });

        options.TokenValidationParameters.ValidAudience = "APP_DSE";
        options.TokenValidationParameters.ValidateIssuerSigningKey = true;

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                string? outer = ctx.HttpContext.Request.Cookies["PA.APP_DSS"];
                if (string.IsNullOrEmpty(outer))
                {
                    string authHeader = ctx.HttpContext.Request.Headers.Authorization.ToString();
                    if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        outer = authHeader["Bearer ".Length..].Trim();
                    }
                }

                if (string.IsNullOrEmpty(outer))
                {
                    return Task.CompletedTask;
                }

                var handler = new JwtSecurityTokenHandler();
                if (!handler.CanReadToken(outer))
                {
                    return Task.CompletedTask;
                }

                string? inner = handler.ReadJwtToken(outer).Payload.GetValueOrDefault("access_token") as string;
                if (!string.IsNullOrEmpty(inner))
                {
                    ctx.Token = inner;
                }

                return Task.CompletedTask;
            },
        };
    }

    public void Configure(JwtBearerOptions options) => Configure(PingAuthDefaults.AuthenticationScheme, options);
}

[ExcludeFromCodeCoverage]
public static class PingAuthOptionsExtensions
{
    public static AuthenticationBuilder AddPingAuth(this AuthenticationBuilder builder)
    {
        builder.AddJwtBearer(PingAuthDefaults.AuthenticationScheme);
        builder.Services.ConfigureOptions<ConfigurePingJwtBearerOptions>();
        return builder;
    }
}
