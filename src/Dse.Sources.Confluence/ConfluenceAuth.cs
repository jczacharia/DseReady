// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Auth;
using Microsoft.AspNetCore.Authorization;

namespace Dse.Sources.Confluence;

public static class ConfluenceAuth
{
    public const string ConfluenceUsersEntitlement = "GSGu_CFL_CFLUsers";

    public static AuthorizationPolicyBuilder RequireConfluenceEntitlement(this AuthorizationPolicyBuilder builder) => builder
        .RequireAnyEntitlements(ConfluenceUsersEntitlement);
}
