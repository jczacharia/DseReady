// Copyright (c) PNC Financial Services. All rights reserved.


using Microsoft.Extensions.Hosting;

namespace Dse.Shared;

public static class EnvironmentExtensions
{
    public static bool IsTest(this IHostEnvironment environment) => environment.IsEnvironment("Test");
}
