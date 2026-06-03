// Copyright (c) PNC Financial Services. All rights reserved.


using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dse.Auth;

public sealed class LdapClaimsEnrichmentMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IServiceProvider services)
    {
        foreach (ClaimsIdentity existingId in context.User.Identities.Where(i => i.IsAuthenticated))
        {
            if (existingId.FindFirst(ClaimTypes.NameIdentifier)?.Value is { Length: > 0 } uid)
            {
                foreach (LdapConnector connector in services.GetServices<LdapConnector>())
                {
                    var identity = new ClaimsIdentity(connector.Name);

                    foreach (string membership in await connector.GetMembershipsAsync(uid))
                    {
                        identity.AddClaim(new Claim(ClaimTypes.Role, membership));
                    }

                    context.User.AddIdentity(identity);
                }
            }
        }

        await next(context);
    }
}
