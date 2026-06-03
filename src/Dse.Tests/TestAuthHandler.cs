// Copyright (c) PNC Financial Services. All rights reserved.


using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dse.Tests;

public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Context.Items.ContainsKey("TestUid") || Context.Items["TestUid"] is not string uid)
        {
            return Task.FromResult(AuthenticateResult.Fail("UserTenant not found in HttpContext.Items"));
        }

        var identity = new ClaimsIdentity(Scheme.Name);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, uid));

        if (Context.Items.ContainsKey("TestRoles") && Context.Items["TestRoles"] is string[] roles)
        {
            identity.AddClaims(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        }

        ClaimsPrincipal principal = new(identity);
        AuthenticationTicket ticket = new(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
