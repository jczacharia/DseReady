// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Claims;
using Dse.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Dse.Auth;

[ExcludeFromCodeCoverage]
public static class AuthExtensions
{
    public static bool IsAnonymous(this ClaimsPrincipal user) =>
        user.Identity is null || !user.Identities.Any(i => i.IsAuthenticated);

    public static AuthorizationPolicyBuilder RequireAllEntitlements(
        this AuthorizationPolicyBuilder builder,
        params string[] entitlements) => builder.RequireEntitlementsCore(entitlements, orOperator: false);

    public static AuthorizationPolicyBuilder RequireAnyEntitlements(
        this AuthorizationPolicyBuilder builder,
        params string[] entitlements) => builder.RequireEntitlementsCore(entitlements, orOperator: true);

    private static AuthorizationPolicyBuilder RequireEntitlementsCore(
        this AuthorizationPolicyBuilder builder,
        string[] entitlements,
        bool orOperator)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(entitlements.Length, other: 0, nameof(entitlements));
        return builder
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
            {
                if (context.Resource is not DefaultHttpContext httpContext || httpContext.User.IsAnonymous())
                {
                    return Task.FromResult(false);
                }

                if (orOperator
                        ? entitlements.Any(e => httpContext.User.IsInRole(e))
                        : entitlements.All(e => httpContext.User.IsInRole(e)))
                {
                    return Task.FromResult(true);
                }

                httpContext.SetProblem(HttpStatusCode.Forbidden, "Missing Entitlements",
                    "User does not have the required entitlements to perform this action.");
                return Task.FromResult(false);
            });
    }

    public static AuthorizationPolicyBuilder RequireKibanaAdminEntitlement(this AuthorizationPolicyBuilder builder) => builder
        .RequireAllEntitlements(DseEntitlements.KibanaAdminOudDn);

    public static AuthorizationPolicyBuilder RequireKibanaReadonlyEntitlement(this AuthorizationPolicyBuilder builder) => builder
        .RequireAnyEntitlements(DseEntitlements.KibanaAdminOudDn, DseEntitlements.KibanaReadonlyOudDn);

    public static ProblemDetails InsufficientEntitlementsProblem() => new()
    {
        Status = (int)HttpStatusCode.Forbidden,
        Title = "Insufficient Entitlements",
        Detail = "User does not have the required entitlements to perform this action.",
    };
}
