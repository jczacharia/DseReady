// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dse.Auth;

[ExcludeFromCodeCoverage]
public sealed class DevAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    IDseEnvironment dseEnv,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (dseEnv is not IDseLocalEnvironment localEnv)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // Pass the scheme name as the authentication type so the identity reports IsAuthenticated — otherwise
        // RequireAuthenticatedUser policies reject it and the claims-enrichment middleware skips it.
        ClaimsIdentity identity = new(Scheme.Name);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, localEnv.Username));
        foreach (string role in localEnv.Roles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        ClaimsPrincipal principal = new(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
