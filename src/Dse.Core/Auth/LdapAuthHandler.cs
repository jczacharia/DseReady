// Copyright (c) PNC Financial Services. All rights reserved.


using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dse.Auth;

public class LdapAuthHandler(
    IOptionsMonitor<LdapAuthOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    DseEnvironment env,
    IServiceProvider services)
    : AuthenticationHandler<LdapAuthOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? uid = null;
        if (env is DseEnvironment.Dev devEnv)
        {
            uid = devEnv.Username;
        }
        else if (await Context.AuthenticateAsync(PingAuthDefaults.AuthenticationScheme) is { Succeeded: true } pingResult)
        {
            uid = pingResult.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        }

        if (string.IsNullOrEmpty(uid))
        {
            return AuthenticateResult.Fail("No user identifier resolved.");
        }

        var identity = new ClaimsIdentity(Scheme.Name);
        var connector = services.GetRequiredKeyedService<LdapConnector>(Scheme.Name);

        foreach (string membership in await connector.GetMembershipsAsync(uid))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, membership));
        }

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }
}
