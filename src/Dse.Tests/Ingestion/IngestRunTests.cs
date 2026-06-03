// Copyright (c) PNC Financial Services. All rights reserved.


using AwesomeAssertions;
using Dse.Auth;
using Dse.Ingestion;

namespace Dse.Tests.Ingestion;

public class IngestRunTests(ITestOutputHelper toh, TestFixture fixture) : TestBed(toh, fixture)
{
    [Fact]
    public Task FullIngest() => ForEachSourceAsync(module => Assert.MultipleAsync(
        () => Problem(s =>
        {
            s.Post.Url($"/api/sources/{module.SourceKey}/ingest/full");
            s.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        }),
        () => Problem(s =>
        {
            s.Post.Url($"/api/sources/{module.SourceKey}/ingest/full");
            s.WithUser();
            s.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        }),
        () => TrackedScenario(s =>
        {
            s.Post.Url($"/api/sources/{module.SourceKey}/ingest/full");
            s.WithUser([DseEntitlements.KibanaAdminOudDn]);
            s.StatusCodeShouldBe(HttpStatusCode.Accepted);
        }, (tracked, _) => tracked.FindSingleTrackedMessageOfType<IngestRunCreated>().Should().NotBeNull())
    ));
}
