// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
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

        options.TokenValidationParameters.ValidateIssuer = false;
        options.TokenValidationParameters.ValidateAudience = false;
        options.TokenValidationParameters.ValidateIssuerSigningKey = true;
        options.TokenValidationParameters.TransformBeforeSignatureValidation = (token, _) =>
        {
            if (token is JsonWebToken outer
                && outer.TryGetPayloadValue<string>("access_token", out string? inner)
                && !string.IsNullOrEmpty(inner))
            {
                return new JsonWebToken(inner);
            }

            return token;
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                if (ctx.HttpContext.Request.Cookies["PA.APP_DSS"] is { Length: > 0 } cookieJwt)
                {
                    ctx.Token = cookieJwt;
                }

                return Task.CompletedTask;
            },
            OnTokenValidated = ctx =>
            {
                if (ctx.Principal?.Identity is ClaimsIdentity identity
                    && identity.FindFirst("uid")?.Value is { Length: > 0 } uid)
                {
                    identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, uid));
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
