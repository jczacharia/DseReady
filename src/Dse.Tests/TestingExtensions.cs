// Copyright (c) PNC Financial Services. All rights reserved.


namespace Dse.Tests;

public static class TestingExtensions
{
    public static void WithUser(this Scenario scenario, string uid, params string[] roles)
    {
        scenario.ConfigureHttpContext(httpContext =>
        {
            httpContext.Items["TestUid"] = uid;
            httpContext.Items["TestRoles"] = roles;
        });
    }
}
