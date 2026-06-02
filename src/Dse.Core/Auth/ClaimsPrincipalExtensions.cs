// Copyright (c) PNC Financial Services. All rights reserved.


using System.Security.Claims;

namespace Dse.Auth;

public static class ClaimsPrincipalExtensions
{
    public static bool IsAnonymous(this ClaimsPrincipal user) =>
        user.Identity is null || !user.Identities.Any(i => i.IsAuthenticated);
}
