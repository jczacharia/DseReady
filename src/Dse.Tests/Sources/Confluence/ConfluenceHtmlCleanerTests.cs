// Copyright (c) PNC Financial Services. All rights reserved.


using AwesomeAssertions;
using Dse.Sources.Confluence;

namespace Dse.Tests.Sources.Confluence;

public sealed class ConfluenceHtmlCleanerTests
{
    private static string Clean(string? input) => ConfluenceHtmlCleaner.Clean(input);

    [Fact]
    public void NullInput_ReturnsEmpty() => Clean(null).Should().BeEmpty();

    [Fact]
    public void EmptyInput_ReturnsEmpty() => Clean(string.Empty).Should().BeEmpty();

    [Fact]
    public void WhitespaceOnly_ReturnsEmpty() => Clean("   \n\t  ").Should().BeEmpty();

    [Fact]
    public void PlainParagraph_ReturnsTrimmedText() =>
        Clean("<p>hello world</p>").Should().Be("hello world");

    [Fact]
    public void MultipleParagraphs_AreSeparatedBySpace()
    {
        string clean = Clean("<p>first</p><p>second</p>");
        clean.Should().Contain("first");
        clean.Should().Contain("second");
        clean.Should()
            .NotContain("firstsecond",
                "adjacent block elements must produce a word boundary for the analyzer");
    }

    [Fact]
    public void CodeBlockMacro_PreservesCodeIdentifiersForSearch()
    {
        const string fixture = """
                               <p>before</p><ac:structured-macro ac:name="code" ac:schema-version="1" ac:macro-id="f0f04eed-9e93-45d0-be15-23b47f5fe5d2"><ac:parameter ac:name="language">csharp</ac:parameter><ac:plain-text-body><![CDATA[Console.WriteLine("hello searchable");
                               public class Foo { }]]></ac:plain-text-body></ac:structured-macro><p>after</p>
                               """;

        string clean = Clean(fixture);
        clean.Should().Contain("before");
        clean.Should().Contain("after");
        clean.Should().Contain("Console.WriteLine", "code identifiers are valuable search terms");
        clean.Should().Contain("hello searchable");
        clean.Should().Contain("public class Foo");
        clean.Should().NotContain("csharp", "the language parameter is metadata, not document content");
        clean.Should().NotContain("macro-id", "macro identifiers must never leak into the indexed body");
        clean.Should().NotContain("CDATA");
    }

    [Fact]
    public void InfoMacroWithRichTextBody_PreservesBodyForSearch()
    {
        const string fixture = """
                               <p>intro</p><ac:structured-macro ac:name="info" ac:schema-version="1" ac:macro-id="671680e7-a328-436c-9049-96334007b71d"><ac:rich-text-body><p>This is critical information that must be searchable.</p></ac:rich-text-body></ac:structured-macro><p>outro</p>
                               """;

        string clean = Clean(fixture);
        clean.Should().Contain("intro");
        clean.Should().Contain("outro");
        clean.Should()
            .Contain("critical information",
                "info-box content is meaningful and must be retained for relevance");
        clean.Should().Contain("must be searchable");
    }

    [Fact]
    public void ExpandMacro_PreservesTitleAndBody()
    {
        const string fixture = """
                               <ac:structured-macro ac:name="expand" ac:schema-version="1" ac:macro-id="df68820b-f5b9-4c60-b83f-185ab37e97f6"><ac:parameter ac:name="title">Click me</ac:parameter><ac:rich-text-body><p>Hidden expandable content with keywords.</p></ac:rich-text-body></ac:structured-macro>
                               """;

        string clean = Clean(fixture);
        clean.Should().Contain("Hidden expandable content");
        clean.Should().Contain("keywords");
        clean.Should()
            .NotContain("Click me",
                "macro parameters (including expand titles) are config metadata, not authored body content; the title is also redundant with the page title for search purposes");
    }

    [Fact]
    public void PageLinkWithoutBody_UsesContentTitleAsAnchor()
    {
        const string fixture = """<p>See <ac:link><ri:page ri:content-title="Quarterly Report" /></ac:link> for details.</p>""";

        string clean = Clean(fixture);
        clean.Should().Contain("See");
        clean.Should()
            .Contain("Quarterly Report",
                "without an explicit anchor text, the link target's title is the only searchable signal");
        clean.Should().Contain("for details");
        clean.Should().NotContain("ri:page");
        clean.Should().NotContain("content-title");
    }

    [Fact]
    public void PageLinkWithPlainTextBody_UsesBodyAsAnchor()
    {
        const string fixture =
            """<p>Click <ac:link><ri:page ri:content-title="Some Target" /><ac:plain-text-link-body><![CDATA[here for guidance]]></ac:plain-text-link-body></ac:link>.</p>""";

        string clean = Clean(fixture);
        clean.Should().Contain("Click");
        clean.Should().Contain("here for guidance", "explicit link body wins over the link target title");
        clean.Should()
            .NotContain("Some Target",
                "if the author wrote a body, the page title is redundant and would dilute the anchor signal");
    }

    [Fact]
    public void ExternalUrlAnchor_PreservesVisibleText()
    {
        const string fixture = """<p>Visit <a href="https://example.com/docs">our public docs</a> online.</p>""";

        string clean = Clean(fixture);

        clean.Should().Contain("Visit");
        clean.Should().Contain("our public docs", "anchor text is the searchable representation of a link");
        clean.Should().Contain("online");
        clean.Should().NotContain("example.com", "raw URLs add no semantic value to the body index");
    }

    [Fact]
    public void AttachmentLink_UsesFilenameAsAnchor()
    {
        const string fixture = """<p>Download <ac:link><ri:attachment ri:filename="report-q3.pdf" /></ac:link>.</p>""";

        string clean = Clean(fixture);
        clean.Should().Contain("Download");
        clean.Should()
            .Contain("report-q3.pdf",
                "attachment filenames carry domain meaning (e.g., report-q3) and should be searchable");
    }

    [Fact]
    public void UserMention_IsStrippedBecauseUserKeyHasNoSearchValue()
    {
        const string fixture = """<p>Reach out to <ac:link><ri:user ri:userkey="abc123def" /></ac:link> for help.</p>""";

        string clean = Clean(fixture);
        clean.Should().Contain("Reach out");
        clean.Should().Contain("for help");
        clean.Should()
            .NotContain("abc123def",
                "opaque userkey GUIDs are noise — they would never match a meaningful search query");
    }

    [Fact]
    public void InlineFormatting_FlattenedWhilePreservingTokens()
    {
        const string fixture =
            "<h1>Main Title</h1><p>This is <strong>bold</strong> and <em>italic</em> with <code>inlineCode()</code>.</p><h2>Subsection</h2><p>Plain text.</p>";

        string clean = Clean(fixture);
        clean.Should().Contain("Main Title");
        clean.Should().Contain("bold");
        clean.Should().Contain("italic");
        clean.Should().Contain("inlineCode()", "code identifiers are valuable for technical-doc search");
        clean.Should().Contain("Subsection");
        clean.Should().Contain("Plain text");
        clean.Should().NotContain("<strong>");
        clean.Should().NotContain("<h1>");
    }

    [Fact]
    public void Table_FlattenedWithCellWordBoundaries()
    {
        const string fixture =
            "<table><tbody><tr><th>Region</th><th>Revenue</th></tr><tr><td>North</td><td>1000</td></tr><tr><td>South</td><td>2000</td></tr></tbody></table>";

        string clean = Clean(fixture);
        clean.Should().Contain("Region");
        clean.Should().Contain("Revenue");
        clean.Should().Contain("North");
        clean.Should().Contain("South");
        clean.Should().Contain("1000");
        clean.Should().NotContain("RegionRevenue", "adjacent table cells must be tokenizable individually");
        clean.Should().NotContain("North1000");
    }

    [Fact]
    public void NestedLists_FlattenedWithItemBoundaries()
    {
        const string fixture =
            "<ul><li>First item<ul><li>Sub one</li><li>Sub two</li></ul></li><li>Second item</li></ul><ol><li>One</li><li>Two</li></ol>";

        string clean = Clean(fixture);
        clean.Should().Contain("First item");
        clean.Should().Contain("Sub one");
        clean.Should().Contain("Sub two");
        clean.Should().Contain("Second item");
        clean.Should().Contain("One");
        clean.Should().Contain("Two");
        clean.Should()
            .NotContain("Sub oneSub two", "list items need word boundaries to tokenize independently");
    }

    [Fact]
    public void TaskList_IsDroppedAlongWithStatusAndId()
    {
        const string fixture =
            "<p>Things to do:</p><ac:task-list><ac:task><ac:task-id>1</ac:task-id><ac:task-status>complete</ac:task-status><ac:task-body>Buy milk</ac:task-body></ac:task><ac:task><ac:task-id>2</ac:task-id><ac:task-status>incomplete</ac:task-status><ac:task-body>Walk dog</ac:task-body></ac:task></ac:task-list><p>Done list.</p>";

        string clean = Clean(fixture);
        clean.Should().Contain("Things to do");
        clean.Should().Contain("Done list");
        clean.Should().NotContain("complete", "task status values are noise");
        clean.Should().NotContain("incomplete");
    }

    [Fact]
    public void EmoticonAndPlaceholder_AreDropped()
    {
        const string fixture =
            """<p>Status <ac:emoticon ac:name="thumbs-up" /> and <ac:placeholder>add note here</ac:placeholder> end.</p>""";

        string clean = Clean(fixture);
        clean.Should().Contain("Status");
        clean.Should().Contain("end");
        clean.Should().NotContain("thumbs-up", "emoticon names are not document content");
        clean.Should()
            .NotContain("add note here", "placeholders are editor scaffolding, not authored content");
    }

    [Fact]
    public void HtmlComments_AreRemoved()
    {
        // Confluence sanitizes comments on save, so we hand-craft the input to cover the case
        // where storage XML arrives via API import, migration, or a future Confluence version.
        const string fixture = "<p>before</p><!-- secret editor note do not index --><p>after</p>";

        string clean = Clean(fixture);
        clean.Should().Contain("before");
        clean.Should().Contain("after");
        clean.Should().NotContain("secret editor note", "HTML comments must never leak into the index");
    }

    [Fact]
    public void ScriptAndStyleTags_AreDropped()
    {
        // Hand-crafted: Confluence storage shouldn't contain these but defense-in-depth matters.
        const string fixture = "<p>visible</p><script>alert('x')</script><style>.body { color: red }</style><p>also visible</p>";

        string clean = Clean(fixture);
        clean.Should().Contain("visible");
        clean.Should().Contain("also visible");
        clean.Should().NotContain("alert", "script bodies must never reach the index");
        clean.Should().NotContain("color: red");
    }

    [Fact]
    public void NestedMacros_PreserveInnermostBody()
    {
        const string fixture = """
                               <ac:structured-macro ac:name="info" ac:schema-version="1" ac:macro-id="1ee163f4-d788-4748-a93b-df3972df09b9"><ac:rich-text-body><p>See this code:</p><ac:structured-macro ac:name="code" ac:schema-version="1" ac:macro-id="b0a85a1c-9212-493a-8385-8d6cf3fad888"><ac:plain-text-body><![CDATA[var x = 42;]]></ac:plain-text-body></ac:structured-macro></ac:rich-text-body></ac:structured-macro>
                               """;

        string clean = Clean(fixture);
        clean.Should().Contain("See this code");
        clean.Should()
            .Contain("var x = 42",
                "code inside a wrapping macro is a common technical-doc pattern and must remain searchable");
    }

    [Fact]
    public void LayoutWithSelfClosingTimeInsideTaskList_PreservesSiblingSectionProse()
    {
        // Real Confluence page that previously rendered as just "RESOURCES Retail Complaints
        // TECH Refactor + HCDB enhancement | 24.7" — the entire KEY DATES section was lost.
        // Root cause: `<time datetime="..." />` inside `<ac:task-body>` parsed as an unclosed
        // `<time>` opener that reparented every following sibling (including the next layout
        // section's prose) into the `ac:task-list` subtree, which RemoveNoise then deleted.
        const string fixture = """
                               <ac:layout><ac:layout-section ac:type="three_equal"><ac:layout-cell><h2><strong>RESOURCES</strong></h2><p><strong style="letter-spacing: 0.0px;"><ac:link><ri:page ri:content-title="Retail Complaints TECH Refactor + HCDB enhancement | 24.7" /></ac:link></strong></p></ac:layout-cell><ac:layout-cell><p><br /></p></ac:layout-cell><ac:layout-cell><ac:task-list>
                               <ac:task>
                               <ac:task-id>226</ac:task-id>
                               <ac:task-status>complete</ac:task-status>
                               <ac:task-body><ac:link><ri:user ri:userkey="8aa4c5d3787c946a0178d183b5a00058" /></ac:link> AGENDA PREP - add monica_meeting-thisweek</ac:task-body>
                               </ac:task>
                               <ac:task>
                               <ac:task-id>227</ac:task-id>
                               <ac:task-status>complete</ac:task-status>
                               <ac:task-body><ac:link><ri:user ri:userkey="8aa4c5d3787c946a0178d183b5a00058" /></ac:link> AGENDA PREP </ac:task-body>
                               </ac:task>
                               <ac:task>
                               <ac:task-id>228</ac:task-id>
                               <ac:task-status>complete</ac:task-status>
                               <ac:task-body><ac:link><ri:user ri:userkey="8aa4c5d3787c946a0178d183b5a00058" /></ac:link> POST MEETING - clean up <time datetime="2024-06-05" /> </ac:task-body>
                               </ac:task>
                               <ac:task>
                               <ac:task-id>229</ac:task-id>
                               <ac:task-status>complete</ac:task-status>
                               <ac:task-body><ac:link><ri:user ri:userkey="8aa4c5d3787c946a0178d183b5a00058" /></ac:link> POST MEETING - send notes </ac:task-body>
                               </ac:task>
                               <ac:task>
                               <ac:task-id>230</ac:task-id>
                               <ac:task-status>complete</ac:task-status>
                               <ac:task-body><ac:link><ri:user ri:userkey="8aa4c5d3787c946a0178d183b5a00058" /></ac:link> POST MEETING - remove label</ac:task-body>
                               </ac:task>
                               </ac:task-list></ac:layout-cell></ac:layout-section><ac:layout-section ac:type="single"><ac:layout-cell><h1><span style="color: rgb(255,0,255);"><strong>KEY DATES</strong></span></h1><p>Today's notes</p><ol><li>Review 24.7 CIA<ol><li>remove 171 and similar titles</li><li>move 65 to Other</li><li>see color-coded notes on CIA</li></ol></li><li>Update on disputes - there is still confusion between MacGyver and EDGE Servicing about what MacGyver should be doing<ol><li>Jared Coy had initially advised MacGyver they would be handling hotfix issues for existing Disputes (Debit/ATM, Mobile, Zelle). The problem Stephanie is observing is that none of the other crews got the same message, so they are sending a lot of work to MacGyver to pick up (i.e. missed requirements, even some new build items); they think MacGyver is the Disputes crew</li><li>Regression testing for Disputes includes over 1100 scripts, many of them cannot be Automated. MacGyver currently has 1 TCOE resource and that person is also responsible for gathering the data for testing. They are getting a new resource on the 17th, however this individual has no EDGE experience. So their current (and only) resource will need to split her time to train the new person. </li><li>So far, none of the testing MacGyver has attempted has worked due to downstream dependencies being blocked. </li><li>Jared is escalating this with Monique</li><li>Kelly Bosley's team, under EDGE servicing, still developing; w/ Melissa</li><li>Caitlin Demmond's team under Modernization also has Disputes work </li></ol></li><li>24.8 - see dashboard below<ol><li>Retail Complaints<ol><li>6288 was a missed requirement; the fields appear and they work, but they aren't marked as required; ECR knows how to address the gap</li><li>6022 was from the backlog based on an INC from a long time ago</li></ol></li><li>Disputes<ol><li>6254 - VE40 blocker because they are investigating</li><li>6205</li></ol></li></ol></li><li>Add to Backlog <ol><li>from Craig Roddis: Can the email provide more instruction in the beginning on what to do next? Something to the effect that says you must re-enter the complaint would work.</li><li>update the HCDB error message: currently says Helpdesk<ol><li>they'll have other errors to update as well</li></ol></li></ol></li></ol><p><br /></p><ac:structured-macro ac:name="expand" ac:schema-version="1" ac:macro-id="0ef74dd1-9cc4-4214-9c0f-bef7b8f25896"><ac:parameter ac:name="title">24.8 DASHBOARD</ac:parameter><ac:rich-text-body><p><ac:structured-macro ac:name="jira" ac:schema-version="1" ac:macro-id="d1e147b7-55da-4204-9fe9-024756333602"><ac:parameter ac:name="server">JIRA</ac:parameter><ac:parameter ac:name="jqlQuery">project in ("EDGE Einstein + MacGyver") AND fixVersion = "EDGE 24.8 (Jul 27)" AND issuetype in (Story, Task, bug) AND status != Rejected AND Team in (265) AND (labels not in (OCManalyzed, ocmbackend) OR labels is EMPTY) AND Team = 265</ac:parameter><ac:parameter ac:name="serverId">6bf76063-23a3-3a41-8456-e2c2f20ed3e8</ac:parameter></ac:structured-macro></p></ac:rich-text-body></ac:structured-macro><p><br /></p><hr /><p><br /></p></ac:layout-cell></ac:layout-section></ac:layout>
                               """;

        string clean = Clean(fixture);

        // First-section prose still survives.
        clean.Should().Contain("RESOURCES");
        clean.Should().Contain("Retail Complaints TECH Refactor + HCDB enhancement | 24.7");

        // Second-section prose — the regression: every line below was being silently dropped.
        clean.Should().Contain("KEY DATES");
        clean.Should().Contain("Today's notes");
        clean.Should().Contain("Review 24.7 CIA");
        clean.Should().Contain("remove 171 and similar titles");
        clean.Should().Contain("move 65 to Other");
        clean.Should().Contain("Update on disputes");
        clean.Should().Contain("Jared Coy");
        clean.Should().Contain("Regression testing for Disputes");
        clean.Should().Contain("Kelly Bosley");
        clean.Should().Contain("Caitlin Demmond");
        clean.Should().Contain("24.8 - see dashboard below");
        clean.Should().Contain("6288 was a missed requirement");
        clean.Should().Contain("6254 - VE40 blocker");
        clean.Should().Contain("Add to Backlog");
        clean.Should().Contain("Craig Roddis");
        clean.Should().Contain("re-enter the complaint");
        clean.Should().Contain("HCDB error message");

        // Task-list contents (and the userkey GUID inside them) must still be dropped.
        clean.Should().NotContain("AGENDA PREP", "task-list bodies are noise and must not survive");
        clean.Should().NotContain("8aa4c5d3787c946a0178d183b5a00058");
        clean.Should().NotContain("complete", "task status values are noise");

        // Macro metadata leaks must still be blocked end-to-end.
        clean.Should().NotContain("24.8 DASHBOARD", "expand titles are config metadata");
        clean.Should().NotContain("0ef74dd1");
        clean.Should().NotContain("schema-version");
        clean.Should().NotContain("jqlQuery");
        clean.Should().NotContain("ac:");
    }

    [Fact]
    public void SelfClosingSpanMidParagraph_DoesNotSwallowFollowingSiblings()
    {
        // Same family of bug as the `<time/>` regression: any non-void HTML tag emitted in
        // XHTML self-closing form would parse as an opener and reparent later siblings.
        // `<span ... />` is one Confluence/migration sources have been seen producing.
        const string fixture =
            "<p>before-span <span class=\"x\" /> middle text</p><p>second paragraph</p><p>third paragraph</p>";

        string clean = Clean(fixture);

        clean.Should().Contain("before-span");
        clean.Should()
            .Contain("middle text",
                "content following a self-closing span on the same paragraph must survive");
        clean.Should()
            .Contain("second paragraph",
                "later sibling paragraphs must not be reparented under an unclosed span");
        clean.Should().Contain("third paragraph");
    }

    [Fact]
    public void CleanWithoutLogger_StillReturnsCleanedTextAndDoesNotThrow()
    {
        // Logger is optional — cleaner must work for tests / scripts / callers that don't have one.
        string clean = ConfluenceHtmlCleaner.Clean("<p>hello</p>");
        clean.Should().Be("hello");
    }

    [Fact]
    public void MacroIdentifiers_NeverLeakIntoIndex()
    {
        // Cross-cuts every macro fixture: a single regression that surfaces the entire family.
        const string fixture = """
                               <ac:structured-macro ac:name="info" ac:schema-version="1" ac:macro-id="671680e7-a328-436c-9049-96334007b71d"><ac:rich-text-body><p>body</p></ac:rich-text-body></ac:structured-macro>
                               """;

        string clean = Clean(fixture);
        clean.Should().NotContain("671680e7");
        clean.Should().NotContain("schema-version");
        clean.Should().NotContain("ac:");
    }

    [Fact]
    public void HtmlMacro_ExtractsTextFromCdataBody_WithoutLeakingTags()
    {
        // Regression: the `html` storage macro wraps real HTML inside `ac:plain-text-body` CDATA.
        // Before macro-aware handling, the unwrapped markup surfaced verbatim, so the index ended
        // up containing visible `<h1>...</h1>`, `<table>`, etc. as tokens.
        const string fixture = """
                               <p>intro</p>
                               <ac:structured-macro ac:name="html" ac:schema-version="1" ac:macro-id="abc-123">
                                 <ac:plain-text-body><![CDATA[<h1>Configuration Comparison</h1>
                               <p><strong>Date:</strong> April 14, 2026</p>
                               <table><thead><tr><th>Aspect</th><th>Value</th></tr></thead>
                               <tbody><tr><td>Base Image</td><td><code>apache-rhel8</code></td></tr></tbody></table>]]></ac:plain-text-body>
                               </ac:structured-macro>
                               <p>outro</p>
                               """;

        string clean = Clean(fixture);

        clean.Should().Contain("intro");
        clean.Should().Contain("outro");
        clean.Should().Contain("Configuration Comparison", "html-macro text content must reach the index");
        clean.Should().Contain("April 14, 2026");
        clean.Should().Contain("Base Image");
        clean.Should().Contain("apache-rhel8");
        clean.Should().NotContain("<h1>", "tag markup from inside an html macro CDATA must not surface as text");
        clean.Should().NotContain("</h1>");
        clean.Should().NotContain("<table>");
        clean.Should().NotContain("<tr>");
        clean.Should().NotContain("<td>");
        clean.Should().NotContain("CDATA");
    }

    [Fact]
    public void CodeMacro_BodyRemainsVerbatim_EvenWhenItLooksLikeMarkup()
    {
        // Counterpart to HtmlMacro_*: a `code` macro's body is source, not markup. Even if the
        // snippet contains angle brackets (Dockerfile FROM, XML config, etc.), they must survive
        // intact so identifiers remain searchable.
        const string fixture = """
                               <ac:structured-macro ac:name="code" ac:schema-version="1">
                                 <ac:parameter ac:name="language">dockerfile</ac:parameter>
                                 <ac:plain-text-body><![CDATA[FROM apache-rhel8:2.4
                               COPY dist /var/www/html
                               ENTRYPOINT ["/usr/bin/run-httpd"]]]></ac:plain-text-body>
                               </ac:structured-macro>
                               """;

        string clean = Clean(fixture);

        clean.Should().Contain("FROM apache-rhel8");
        clean.Should().Contain("COPY dist /var/www/html");
        clean.Should().Contain("ENTRYPOINT");
        clean.Should().NotContain("dockerfile", "the language parameter is metadata");
    }
}
