// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

        options.Audience = "APP_DSE";
        options.Authority = env switch
        {
            DseEnvironment.Rnd => "https://iam-idp-wf-rnd.pncint.net",
            DseEnvironment.Uat => "https://iam-idp-wf-uat.pncint.net",
            DseEnvironment.Qa => "https://wfsso-qa.pnc.com",
            _ => "https://wfsso.pnc.com",
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.HttpContext.Request.Cookies["PA.APP_DSS"] is { Length: > 0 } cookieJwt)
                {
                    context.Token = cookieJwt;
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
