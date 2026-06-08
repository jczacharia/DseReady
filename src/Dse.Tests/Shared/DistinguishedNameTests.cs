// Copyright (c) PNC Financial Services. All rights reserved.


using AwesomeAssertions;
using AwesomeAssertions.Execution;
using Dse.Shared;

namespace Dse.Tests.Shared;

public class DistinguishedNameTests
{
    // ── GetAttribute: happy path ─────────────────────────────────────────────

    [Theory]
    [InlineData("cn=app-dse-kibana-admin,ou=Groups,o=pnc", "cn", "app-dse-kibana-admin")]
    [InlineData("cn=app-dse-kibana-user-readonly,ou=Groups,o=pnc", "cn", "app-dse-kibana-user-readonly")]
    [InlineData("CN=GSGu_CFL_CFLUsers,OU=OUg_Applications,DC=pncbank,DC=com", "cn", "GSGu_CFL_CFLUsers")]
    public void GetAttribute_Cn_ReturnsExpectedValue(string raw, string attr, string expected)
    {
        DistinguishedName dn = DistinguishedName.Parse(raw);
        dn.GetAttribute(attr).ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("cn=app-dse-kibana-admin,ou=Groups,o=pnc", "o", "pnc")]
    [InlineData("cn=foo,ou=Groups,o=pnc", "ou", "Groups")]
    [InlineData("CN=GSGu_CFL_CFLUsers,DC=pncbank,DC=com", "dc", "pncbank")]
    public void GetAttribute_NonCnAttributes_ReturnsExpectedValue(string raw, string attr, string expected)
    {
        DistinguishedName dn = DistinguishedName.Parse(raw);
        dn.GetAttribute(attr).ToString().Should().Be(expected);
    }

    // ── Case insensitivity ───────────────────────────────────────────────────

    [Theory]
    [InlineData("cn")]
    [InlineData("CN")]
    [InlineData("Cn")]
    [InlineData("cN")]
    public void GetAttribute_IsCaseInsensitiveOnAttributeType(string attr)
    {
        DistinguishedName dn = DistinguishedName.Parse("cn=app-dse-kibana-admin,ou=Groups,o=pnc");
        dn.GetAttribute(attr).ToString().Should().Be("app-dse-kibana-admin");
    }

    [Theory]
    [InlineData("CN=GSGu_CFL_CFLUsers,ou=Groups,o=pnc")]
    [InlineData("Cn=GSGu_CFL_CFLUsers,ou=Groups,o=pnc")]
    public void GetAttribute_IsCaseInsensitiveOnDnPrefix(string raw)
    {
        DistinguishedName dn = DistinguishedName.Parse(raw);
        dn.GetAttribute("cn").ToString().Should().Be("GSGu_CFL_CFLUsers");
    }

    // ── Missing attribute ────────────────────────────────────────────────────

    [Theory]
    [InlineData("cn=foo,ou=Groups,o=pnc", "dc")]
    [InlineData("CN=GSGu_CFL_CFLUsers,DC=pncbank,DC=com", "o")]
    public void GetAttribute_WhenAttributeAbsent_ReturnsEmpty(string raw, string attr)
    {
        DistinguishedName dn = DistinguishedName.Parse(raw);
        dn.GetAttribute(attr).IsEmpty.Should().BeTrue();
    }

    [Theory]
    [InlineData("cn=foo,ou=Groups,o=pnc", "dc")]
    [InlineData("CN=GSGu_CFL_CFLUsers,DC=pncbank,DC=com", "o")]
    public void GetAttributeString_WhenAttributeAbsent_ReturnsNull(string raw, string attr)
    {
        DistinguishedName dn = DistinguishedName.Parse(raw);
        dn.GetAttributeString(attr).Should().BeNull();
    }

    // ── Malformed input ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("noequals")]
    [InlineData("ou=Groups,o=pnc")] // no cn RDN
    public void GetAttribute_MalformedOrMissingCn_ReturnsEmpty(string raw)
    {
        DistinguishedName dn = DistinguishedName.Parse(raw);
        dn.GetAttribute("cn").IsEmpty.Should().BeTrue();
    }

    // ── Value-only RDN (no trailing comma) ───────────────────────────────────

    [Fact]
    public void GetAttribute_SingleRdn_ReturnsValueWithoutComma()
    {
        DistinguishedName dn = DistinguishedName.Parse("cn=standalone");
        dn.GetAttribute("cn").ToString().Should().Be("standalone");
    }

    // ── Record struct equality ───────────────────────────────────────────────

    [Fact]
    public void Equality_SameDn_AreEqual()
    {
        DistinguishedName a = DistinguishedName.Parse("cn=foo,o=pnc");
        DistinguishedName b = DistinguishedName.Parse("cn=foo,o=pnc");
        a.Should().Be(b);
    }

    [Fact]
    public void Equality_DifferentDn_AreNotEqual()
    {
        DistinguishedName a = DistinguishedName.Parse("cn=foo,o=pnc");
        DistinguishedName b = DistinguishedName.Parse("cn=bar,o=pnc");
        a.Should().NotBe(b);
    }

    // ── ToString passthrough ─────────────────────────────────────────────────

    [Fact]
    public void ToString_ReturnsOriginalValue()
    {
        const string raw = "cn=app-dse-kibana-admin,ou=Groups,o=pnc";
        DistinguishedName.Parse(raw).ToString().Should().Be(raw);
    }

    // ── AssertionScope: multi-assert without short-circuit ───────────────────

    [Fact]
    public void GetAttribute_FullDn_AllRdnsResolveCorrectly()
    {
        DistinguishedName dn = DistinguishedName.Parse("cn=app-dse-kibana-admin,ou=Groups,o=pnc");

        using var scope = new AssertionScope();
        dn.GetAttributeString("cn").Should().Be("app-dse-kibana-admin");
        dn.GetAttributeString("ou").Should().Be("Groups");
        dn.GetAttributeString("o").Should().Be("pnc");
        dn.GetAttributeString("dc").Should().BeNull();
    }
}
