// Copyright (c) PNC Financial Services. All rights reserved.


using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Dse.Shared;
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
public sealed class PingOptions
{
    public const string SchemeName = "Ping";

    [Url]
    [Required]
    public string MetadataAddress { get; set; } = string.Empty;

    [Required]
    public string CookieName { get; set; } = "PA.APP_DSE";

    [Required]
    public string IsMemberOfClaim { get; set; } = "isMemberOf";

    [Required]
    public string UidClaim { get; set; } = "uid";

    [Required]
    public string AccessTokenClaim { get; set; } = "access_token";

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
        return builder.AddJwtBearer(PingOptions.SchemeName);
    }
}
