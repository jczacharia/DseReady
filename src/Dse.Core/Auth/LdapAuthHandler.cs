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
    IServiceProvider services)
    : AuthenticationHandler<LdapAuthOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.User.IsAnonymous() || Context.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { Length: > 0 } uid)
        {
            return AuthenticateResult.NoResult();
        }

        var identity = new ClaimsIdentity(); // Authentication type intentionally null
        var connector = services.GetRequiredKeyedService<LdapConnector>(Scheme.Name);

        foreach (string membership in await connector.GetMembershipsAsync(uid))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, membership));
        }

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }
}
