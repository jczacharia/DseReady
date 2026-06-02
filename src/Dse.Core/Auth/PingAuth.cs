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
public static class PingAuthOptionsExtensions
{
    public static AuthenticationBuilder AddPingAuth(this AuthenticationBuilder builder)
    {
        builder.AddJwtBearer(PingAuthDefaults.AuthenticationScheme);
        builder.Services.ConfigureOptions<ConfigurePingJwtBearerOptions>();
        return builder;
    }
}
