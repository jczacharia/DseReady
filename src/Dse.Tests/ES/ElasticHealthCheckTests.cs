// Copyright (c) PNC Financial Services. All rights reserved.


namespace Dse.Tests.ES;

/// <summary>
///     Exercises <c>ElasticHealthCheck</c> through its diagnostic per-check route (<c>/api/health/elastic</c>) against
///     the live Elasticsearch — a green or yellow cluster maps to a 200, which is what the platform's readiness
///     gate (<c>/ready</c>) ultimately surfaces. Hitting the per-check route isolates ES from the other
///     readiness-tagged checks so a flaky LDAP can't mask an ES regression.
/// </summary>
public sealed class ElasticHealthCheckTests(ITestOutputHelper toh, TestFixture fixture) : TestBed(toh, fixture)
{
    [Fact]
    public async Task ReadyEndpoint_WithLiveCluster_ReportsHealthy() =>
        await Scenario(s =>
        {
            s.Get.Url("/api/health/elastic");
            s.StatusCodeShouldBeOk();
        });
}
