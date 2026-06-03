// Copyright (c) PNC Financial Services. All rights reserved.


namespace Dse.Tests;

public static class TestingExtensions
{
    public static void WithUser(this Scenario s, string? uid = null, params string[] roles) => s.ConfigureHttpContext(context =>
    {
        context.Items[TestAuthHandler.TestUid] = uid ?? Guid.NewGuid().ToString("N")[..7];
        context.Items[TestAuthHandler.TestRoles] = roles;
    });

    public static void WithUser(this Scenario s, string[] roles) => s.ConfigureHttpContext(context =>
    {
        context.Items[TestAuthHandler.TestUid] = Guid.NewGuid().ToString("N")[..7];
        context.Items[TestAuthHandler.TestRoles] = roles;
    });
}
