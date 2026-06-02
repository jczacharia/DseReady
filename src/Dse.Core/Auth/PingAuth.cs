// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

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

        if (name != PingAuthDefaults.AuthenticationScheme)
        {
            return;
        }


        // No Authority — there is no OIDC discovery doc for a PA web session.
        options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            env switch
            {
                DseEnvironment.Rnd => "https://iam-idp-wf-rnd.pncint.net/pa/authtoken/JWKS",
                DseEnvironment.Uat => "https://iam-idp-wf-uat.pncint.net/pa/authtoken/JWKS",
                DseEnvironment.Qa => "https://wfsso-qa.pnc.com/pa/authtoken/JWKS",
                _ => "https://wfsso.pnc.com/pa/authtoken/JWKS",
            },
            new JwksRetriever(),
            new HttpDocumentRetriever { RequireHttps = true });

        options.TokenValidationParameters.ValidIssuer = "PingAccess";
        options.TokenValidationParameters.ValidAudience = "APP_DSE";
        options.TokenValidationParameters.ValidateIssuerSigningKey = true;

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                if (ctx.HttpContext.Request.Cookies["PA.APP_DSS"] is { Length: > 0 } jwt)
                {
                    ctx.Token = jwt;
                }

                return Task.CompletedTask;
            },
            OnAuthenticationFailed = ctx =>
            {
                var raw = ctx.Request.Headers.Authorization.ToString();
                var tokenStr = raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? raw.Substring(7).Trim()
                    : raw;

                try
                {
                    var jwt = new JwtSecurityTokenHandler().ReadJwtToken(tokenStr);
                    Console.WriteLine($"alg={jwt.Header.Alg} kid='{jwt.Header.Kid}' typ={jwt.Header.Typ}");
                    Console.WriteLine($"raw header: {Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(jwt.RawHeader))}");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"parse failed: {e.Message}");
                }

                Console.WriteLine($"failure: {ctx.Exception}");
                return Task.CompletedTask;
            }
        };
    }

    public void Configure(JwtBearerOptions options) => Configure(PingAuthDefaults.AuthenticationScheme, options);
}

[ExcludeFromCodeCoverage]
internal sealed class JwksRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address,
        IDocumentRetriever retriever,
        CancellationToken ct)
    {
        string? json = await retriever.GetDocumentAsync(address, ct);
        var keys = new JsonWebKeySet(json);
        var cfg = new OpenIdConnectConfiguration { Issuer = "PingAccess" };

        foreach (SecurityKey? k in keys.GetSigningKeys())
        {
            cfg.SigningKeys.Add(k);
        }

        return cfg;
    }
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
