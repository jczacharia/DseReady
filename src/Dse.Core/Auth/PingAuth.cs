// Copyright (c) PNC Financial Services. All rights reserved.


using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Dse.Auth;

/// <summary>Configures PingFederate JWT-bearer auth: where the token lives and which claims to read from it.</summary>
public sealed class PingOptions
{
    public const string SchemeName = "Ping";

    /// <summary>OIDC discovery document URL used to fetch the issuer's signing keys.</summary>
    [Url]
    [Required]
    public string MetadataAddress { get; set; } = string.Empty;

    /// <summary>Cookie the token is pulled from when the request carries no bearer header.</summary>
    [Required]
    public string CookieName { get; set; } = "PA.APP_DSE";

    /// <summary>Claim carrying caret-separated group memberships, each mapped to a role.</summary>
    [Required]
    public string IsMemberOfClaim { get; set; } = "isMemberOf";

    /// <summary>Claim holding the user id, promoted to the <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/> and used for LDAP enrichment.</summary>
    [Required]
    public string UidClaim { get; set; } = "uid";

    /// <summary>Outer-token claim whose nested JWT is the real access token to validate.</summary>
    [Required]
    public string AccessTokenClaim { get; set; } = "access_token";

    /// <summary>Response header naming this scheme on a 401 challenge.</summary>
    [Required]
    public string ChallengeHeader { get; set; } = "X-Auth-Challenge";
}

[ExcludeFromCodeCoverage]
public static class PingAuthOptionsExtensions
{
    public static AuthenticationBuilder AddPingJwtBearer(this AuthenticationBuilder builder)
    {
        builder.Services
            .AddOptions<PingOptions>()
            .BindConfiguration(PingOptions.SchemeName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services
            .AddOptions<JwtBearerOptions>(PingOptions.SchemeName)
            .Configure<IOptionsMonitor<PingOptions>>((options, ping) =>
            {
                options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    ping.CurrentValue.MetadataAddress,
                    new OpenIdConnectConfigurationRetriever(),
                    new HttpDocumentRetriever { RequireHttps = true });

                options.TokenValidationParameters.ValidateIssuer = false;
                options.TokenValidationParameters.ValidateAudience = false;
                options.TokenValidationParameters.ValidateIssuerSigningKey = true;
                options.TokenValidationParameters.TransformBeforeSignatureValidation = (token, _) =>
                {
                    if (token is JsonWebToken outer
                        && outer.TryGetPayloadValue<string>(ping.CurrentValue.AccessTokenClaim, out string? inner)
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
                        if (context.HttpContext.Request.Cookies[ping.CurrentValue.CookieName] is { Length: > 0 } cookieJwt)
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

                        if (identity.FindFirst(ping.CurrentValue.UidClaim)?.Value is { Length: > 0 } uid)
                        {
                            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, uid));
                            foreach (LdapConnector connector in context.HttpContext.RequestServices.GetServices<LdapConnector>())
                            {
                                await connector.AddMembershipsClaims(identity, uid);
                            }
                        }


                        if (identity.FindFirst(ping.CurrentValue.IsMemberOfClaim)?.Value is { Length: > 0 } memberOf)
                        {
                            foreach (string member in memberOf.Split(separator: '^',
                                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            {
                                identity.AddClaim(new Claim(ClaimTypes.Role, member));
                            }
                        }
                    },
                    OnChallenge = context =>
                    {
                        context.Response.Headers.Append(ping.CurrentValue.ChallengeHeader, context.Scheme.Name);
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.HandleResponse();
                        return Task.CompletedTask;
                    },
                };
            });
        builder.AddJwtBearer(PingOptions.SchemeName);
        return builder;
    }
}
