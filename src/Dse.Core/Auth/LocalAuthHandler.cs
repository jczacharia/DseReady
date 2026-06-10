// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dse.Auth;

[ExcludeFromCodeCoverage]
public sealed class LocalAuthOptions : AuthenticationSchemeOptions
{
    public const string SchemeName = "Local";

    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool UseLdapConnectors { get; set; } = true;
    public string[] Roles { get; set; } = [];
}

[ExcludeFromCodeCoverage]
public sealed class LocalAuthHandler(
    IOptionsMonitor<LocalAuthOptions> options,
    ILoggerFactory logger,
    IEnumerable<LdapConnector> connectors,
    UrlEncoder encoder) : AuthenticationHandler<LocalAuthOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        ClaimsIdentity identity = new(Scheme.Name);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, Options.Username));
        identity.AddClaims(Options.Roles.Select(r => new Claim(ClaimTypes.Role, r)));

        if (Options.UseLdapConnectors)
        {
            foreach (LdapConnector connector in connectors)
            {
                await connector.AddMembershipsClaims(identity, Options.Username).ConfigureAwait(false);
            }
        }

        ClaimsPrincipal principal = new(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}

[ExcludeFromCodeCoverage]
public static class LocalAuthExtensions
{
    public static AuthenticationBuilder AddLocalAuthentication(this AuthenticationBuilder builder)
    {
        builder.Services
            .AddOptions<LocalAuthOptions>(LocalAuthOptions.SchemeName)
            .BindConfiguration(DseOptions.SectionName)
            .BindConfiguration(LocalAuthOptions.SchemeName)
            .Configure<IHostEnvironment>((options, env) =>
            {
                if (env.IsProduction())
                {
                    options.Username = string.Empty;
                    options.Password = string.Empty;
                    options.Roles = [];
                    options.ForwardDefault = PingOptions.SchemeName;
                }
            });
        builder.AddScheme<LocalAuthOptions, LocalAuthHandler>(LocalAuthOptions.SchemeName, configureOptions: null);
        return builder;
    }
}
