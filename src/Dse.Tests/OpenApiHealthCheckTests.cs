// Copyright (c) PNC Financial Services. All rights reserved.


using AwesomeAssertions;

namespace Dse.Tests;

/// <summary>
///     <c>MapHealthChecks</c> endpoints are invisible to the ApiExplorer, so <c>HealthCheckDocumentFilter</c> injects
///     them into the OpenAPI document. This proves the fixed probes and one route per registered check (driven off
///     the same registrations the runtime maps) all land in the generated <c>swagger.json</c>.
/// </summary>
public sealed class OpenApiHealthCheckTests(ITestOutputHelper toh, TestFixture fixture) : TestBed(toh, fixture)
{
    [Fact]
    public async Task Swagger_document_advertises_the_health_endpoints()
    {
        IScenarioResult result = await Scenario(s =>
        {
            s.Get.Url("/swagger/v1/swagger.json");
            s.StatusCodeShouldBeOk();
        });

        string json = await result.ReadAsTextAsync();

        string[] expected =
        [
            "/live", "/ready", "/health", "/health/sources",
            "/health/elastic", "/health/ldap-ad", "/health/ldap-oud",
        ];
        foreach (string path in expected)
        {
            json.Should().Contain($"\"{path}\"", $"the OpenAPI document should advertise {path}");
        }

        json.Should().Contain("\"Health\"", "the health operations should be grouped under a Health tag");
    }
}
