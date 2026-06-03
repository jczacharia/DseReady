// Copyright (c) PNC Financial Services. All rights reserved.


using System.Net;
using Dse.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Dse.Auth;

public static class AuthExtensions
{
    public static AuthorizationPolicyBuilder RequireEntitlements(this AuthorizationPolicyBuilder builder, string role) => builder
        .RequireAuthenticatedUser()
        .RequireAssertion(context =>
        {
            if (context.Resource is not DefaultHttpContext httpContext)
            {
                return Task.FromResult(false);
            }

            if (httpContext.User.IsInRole(role))
            {
                return Task.FromResult(true);
            }

            httpContext.SetProblem(HttpStatusCode.Forbidden, "Missing Entitlements",
                "User does not have the required entitlements to perform this action.");
            return Task.FromResult(false);
        });

    public static AuthorizationPolicyBuilder RequireKibanaAdminEntitlement(this AuthorizationPolicyBuilder builder) => builder
        .RequireRole(DseEntitlements.KibanaAdminOudDn);

    public static AuthorizationPolicyBuilder RequireKibanaReadonlyEntitlement(this AuthorizationPolicyBuilder builder) => builder
        .RequireRole(DseEntitlements.KibanaReadonlyOudDn);
}
