// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
internal class ConfigurePingJwtBearerOptions(
    ILogger<ConfigurePingJwtBearerOptions> logger,
    IDseEnvironment env,
    IConfiguration cfg) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != PingAuthDefaults.AuthenticationScheme)
        {
            return;
        }

        const string defaultUrl = "https://wfsso-apps.pnc.com/.well-known/openid-configuration";

        string metadataAddress = cfg["Ping:MetadataAddress"] ?? (env is IDseDeploymentEnvironment de
            ? de.Deployment switch
            {
                DeploymentEnvironment.Rnd => "https://wfsso-apps-rnd.pnc.com/.well-known/openid-configuration",
                DeploymentEnvironment.Uat => "https://wfsso-apps-uat.pnc.com/.well-known/openid-configuration",
                DeploymentEnvironment.Qa => "https://wfsso-apps-qa.pnc.com/.well-known/openid-configuration",
                _ => defaultUrl,
            }
            : defaultUrl);

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
            OnMessageReceived = context =>
            {
                if (context.HttpContext.Request.Cookies[cfg["Ping:CookieName"] ?? "PA.APP_DSE"] is { Length: > 0 } cookieJwt)
                {
                    context.Token = cookieJwt;
                }

                return Task.CompletedTask;
            },
            OnTokenValidated = async context =>
            {
                if (context.Principal?.Identity is not ClaimsIdentity identity)
                {
                    return;
                }


                if (identity.FindFirst(cfg["Ping:IsMemberOfClaim"] ?? "isMemberOf")?.Value is { Length: > 0 } memberOf)
                {
                    foreach (string member in memberOf.Split(separator: '^',
                                 StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        identity.AddClaim(new Claim(ClaimTypes.Role, member));
                    }
                }

                if (identity.FindFirst(cfg["Ping:UidClaim"] ?? "uid")?.Value is not { Length: > 0 } uid)
                {
                    return;
                }

                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, uid));

                foreach (LdapConnector connector in context.HttpContext.RequestServices.GetServices<LdapConnector>())
                {
                    if (!connector.Bound)
                    {
                        continue;
                    }

                    try
                    {
                        foreach (string membership in await connector.GetMembershipsAsync(uid))
                        {
                            identity.AddClaim(new Claim(ClaimTypes.Role, membership));
                        }
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "Failed to enrich claims from LDAP {LdapName} for user {Uid}", uid, connector.Name);
                    }
                }
            },
            OnChallenge = context =>
            {
                context.Response.Headers.Append("X-Auth-Challenge", context.Scheme.Name);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.HandleResponse();
                return Task.CompletedTask;
            },
        };
    }

    public void Configure(JwtBearerOptions options) => Configure(PingAuthDefaults.AuthenticationScheme, options);
}

[ExcludeFromCodeCoverage]
public static class PingAuthOptionsExtensions
{
    public static AuthenticationBuilder AddPingAuthentication(this AuthenticationBuilder builder)
    {
        builder.AddJwtBearer(PingAuthDefaults.AuthenticationScheme);
        builder.Services.ConfigureOptions<ConfigurePingJwtBearerOptions>();
        return builder;
    }
}
