// Copyright (c) PNC Financial Services. All rights reserved.


using AwesomeAssertions;

namespace Dse.Tests.Auth;

/// <summary>
///     Each keyed <c>LdapConnector</c> gets its own readiness check on a per-directory diagnostic route
///     (<c>/health/ldap-ad</c>, <c>/health/ldap-oud</c>). No directory is reachable from CI, so each reports
///     unhealthy (503) — what matters is that the route exists and the check labels itself from its own connector
///     (proving one registration per key, not a shared/hardcoded one).
/// </summary>
public sealed class LdapHealthCheckTests(ITestOutputHelper toh, TestFixture fixture) : TestBed(toh, fixture)
{
    [Theory]
    [InlineData("ldap-ad", "ad.dse.test")]
    [InlineData("ldap-oud", "oud.dse.test")]
    public async Task Each_directory_exposes_its_own_self_labeled_health_route(string route, string host)
    {
        IScenarioResult result = await Scenario(s =>
        {
            s.Get.Url($"/health/{route}");
            s.StatusCodeShouldBe(HttpStatusCode.ServiceUnavailable); // unreachable directory ⇒ unhealthy
        });

        string body = await result.ReadAsTextAsync();
        body.Should().Contain(route);
        body.Should().Contain(host, "the check labels itself from its own connector's options");
    }
}
