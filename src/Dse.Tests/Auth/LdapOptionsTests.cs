// Copyright (c) PNC Financial Services. All rights reserved.


using AwesomeAssertions;
using Dse.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dse.Tests.Auth;

/// <summary>
///     AD and OUD are two NAMED instances of the same <see cref="LdapAuthOptions" />, each bound to its own config
///     section, with one <see cref="LdapConnector" /> per directory. The regression these guard: when the options
///     were registered unnamed, the connectors resolved <c>IOptionsMonitor.Get(name)</c> for a name nobody had
///     configured and every field came back <see cref="string.Empty" />.
/// </summary>
public sealed class LdapOptionsTests(ITestOutputHelper toh, TestFixture fixture) : TestBed(toh, fixture)
{
    private IOptionsMonitor<LdapAuthOptions> Monitor => Services.GetRequiredService<IOptionsMonitor<LdapAuthOptions>>();

    [Fact]
    public void Each_named_instance_binds_its_own_section()
    {
        LdapAuthOptions ad = Monitor.Get(LdapAuthDefaults.Ad);
        LdapAuthOptions oud = Monitor.Get(LdapAuthDefaults.Oud);

        ad.Host.Should().Be("ad.dse.test");
        ad.SearchBase.Should().Be("DC=ad,DC=dse,DC=test");
        ad.GroupsAttribute.Should().Be("memberOf");

        oud.Host.Should().Be("oud.dse.test");
        oud.SearchBase.Should().Be("o=dse-oud");
        oud.GroupsAttribute.Should().Be("groupMembership");

        // The whole point of named options: the two sections do not bleed into each other.
        ad.Host.Should().NotBe(oud.Host);
    }

    [Fact]
    public void Post_configure_applies_per_named_instance()
    {
        // A field absent from config (Port) falls through to the section's PostConfigure default, proving the
        // post-configure step is keyed to the same name — not the default instance.
        Monitor.Get(LdapAuthDefaults.Ad).Port.Should().Be(636);
        Monitor.Get(LdapAuthDefaults.Ad).GroupsFilter.Should().NotBeNull();
        Monitor.Get(LdapAuthDefaults.Oud).GroupsFilter.Should().NotBeNull();
    }

    [Fact]
    public void One_connector_per_directory_each_reading_its_own_options()
    {
        LdapConnector[] connectors = Services.GetServices<LdapConnector>().ToArray();

        connectors.Select(c => c.Name)
            .Should()
            .BeEquivalentTo(LdapAuthDefaults.Ad, LdapAuthDefaults.Oud);

        // Each connector reads Monitor.Get(its-own-Name); none of them should see an empty Host (the bug).
        foreach (LdapConnector connector in connectors)
        {
            Monitor.Get(connector.Name).Host.Should().NotBeNullOrEmpty();
        }
    }
}
