// Copyright (c) PNC Financial Services. All rights reserved.


using AwesomeAssertions;
using Dse.Sources.Confluence;
using HtmlAgilityPack;

namespace Dse.Tests.Sources.Confluence;

/// <summary>
///     Pure tests of the anchor rewriting behind the body-view endpoint. The HTML is the real <c>body.view</c>
///     output captured from Confluence (the hard case — internal links are relative and same-page links are bare
///     <c>#fragments</c>), so this verifies the rewrite against what Confluence actually emits.
/// </summary>
public sealed class ConfluenceBodyViewEndpointTests
{
    private const string ViewHtml =
        """
        <h2 id="DSELinkTest-SectionOne">Section One</h2>
        <p>External absolute: <a href="https://www.google.com/search?q=dse" class="external-link" rel="nofollow">Google</a></p>
        <p>Mailto: <a href="mailto:someone@pnc.com" class="external-link" rel="nofollow">Email</a></p>
        <p>Internal page: <a href="/spaces/ds/pages/163932/DSE+Link+Target">Go to target</a></p>
        <p>Same-page anchor: <a href="#DSELinkTest-SectionOne">Jump to Section One</a></p>
        <p>External image: <img class="confluence-embedded-image" src="https://www.example.com/logo.png"></p>
        """;

    private static readonly Uri s_pageUrl = new("http://confluence/spaces/ds/pages/163933/DSE+Link+Test");

    private static List<string> AnchorHrefs(string html)
    {
        HtmlDocument doc = new();
        doc.LoadHtml(html);
        return doc.DocumentNode.SelectNodes("//a[@href]")!
            .Select(a => a.GetAttributeValue("href", string.Empty))
            .ToList();
    }

    [Fact]
    public void EveryAnchor_IsAbsolute_NoRelativeLinksRemain()
    {
        string result = ConfluenceBodyViewEndpoint.AbsolutizeLinks(ViewHtml, s_pageUrl);

        AnchorHrefs(result).Should().OnlyContain(href => Uri.IsWellFormedUriString(href, UriKind.Absolute));
    }

    [Fact]
    public void RelativeInternalLink_ResolvesAgainstThePageUrl()
    {
        string result = ConfluenceBodyViewEndpoint.AbsolutizeLinks(ViewHtml, s_pageUrl);

        AnchorHrefs(result).Should().Contain("http://confluence/spaces/ds/pages/163932/DSE+Link+Target");
    }

    [Fact]
    public void SamePageFragment_ResolvesToThePageUrlPlusFragment()
    {
        string result = ConfluenceBodyViewEndpoint.AbsolutizeLinks(ViewHtml, s_pageUrl);

        AnchorHrefs(result)
            .Should()
            .Contain("http://confluence/spaces/ds/pages/163933/DSE+Link+Test#DSELinkTest-SectionOne");
    }

    [Fact]
    public void AlreadyAbsoluteLinks_AreLeftPointingWhereTheyPoint()
    {
        string result = ConfluenceBodyViewEndpoint.AbsolutizeLinks(ViewHtml, s_pageUrl);

        List<string> hrefs = AnchorHrefs(result);
        hrefs.Should().Contain("https://www.google.com/search?q=dse");
        hrefs.Should().Contain("mailto:someone@pnc.com");
    }

    [Fact]
    public void EveryAnchor_OpensInANewTab()
    {
        string result = ConfluenceBodyViewEndpoint.AbsolutizeLinks(ViewHtml, s_pageUrl);

        HtmlDocument doc = new();
        doc.LoadHtml(result);
        doc.DocumentNode.SelectNodes("//a[@href]")!
            .Should()
            .OnlyContain(a => a.GetAttributeValue("target", string.Empty) == "_blank");
    }

    [Fact]
    public void Images_AreNotTouched()
    {
        string result = ConfluenceBodyViewEndpoint.AbsolutizeLinks(ViewHtml, s_pageUrl);

        result.Should().Contain("src=\"https://www.example.com/logo.png\"");
    }
}
