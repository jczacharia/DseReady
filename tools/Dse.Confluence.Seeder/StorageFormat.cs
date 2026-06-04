// Copyright (c) PNC Financial Services. All rights reserved.


using System.Net;
using System.Text;

namespace Dse.Confluence.Seeder;

// Builders for Confluence storage-format XHTML. Covers every construct ConfluenceHtmlCleaner walks plus
// the wider UI vocabulary (panels, expand, toc, status, jira, emoticons).
public static class StorageFormat
{
    /// <summary>XML-escape text destined for element/attribute content.</summary>
    public static string Esc(string s) => WebUtility.HtmlEncode(s);

    /// <summary>Wrap literal text for a CDATA section (splitting any accidental <c>]]&gt;</c>).</summary>
    private static string Cdata(string s) => "<![CDATA[" + s.Replace("]]>", "]]]]><![CDATA[>") + "]]>";

    // ---- Block text ----

    public static string Heading(int level, string text) => $"<h{level}>{Esc(text)}</h{level}>";

    public static string Para(string innerXhtml) => $"<p>{innerXhtml}</p>";

    public static string Bold(string text) => $"<strong>{Esc(text)}</strong>";

    public static string Italic(string text) => $"<em>{Esc(text)}</em>";

    public static string InlineCode(string text) => $"<code>{Esc(text)}</code>";

    public static string HorizontalRule() => "<hr/>";

    // ---- Links (the headline use case: relative, absolute, root-relative, anchor, attachment, mention) ----

    /// <summary>Relative internal link by page title — resolves within Confluence. Cleaner reads ri:content-title.</summary>
    public static string PageLink(string pageTitle, string? anchorText = null)
    {
        string body = anchorText is null
            ? string.Empty
            : $"<ac:plain-text-link-body>{Cdata(anchorText)}</ac:plain-text-link-body>";
        return $"<ac:link><ri:page ri:content-title=\"{Esc(pageTitle)}\"/>{body}</ac:link>";
    }

    /// <summary>Cross-page anchor link (relative + #fragment).</summary>
    public static string PageAnchorLink(string pageTitle, string anchor, string text) =>
        $"<ac:link ac:anchor=\"{Esc(anchor)}\"><ri:page ri:content-title=\"{Esc(pageTitle)}\"/>" +
        $"<ac:plain-text-link-body>{Cdata(text)}</ac:plain-text-link-body></ac:link>";

    /// <summary>Same-page anchor jump.</summary>
    public static string SamePageAnchorLink(string anchor, string text) =>
        $"<ac:link ac:anchor=\"{Esc(anchor)}\"><ac:plain-text-link-body>{Cdata(text)}</ac:plain-text-link-body></ac:link>";

    /// <summary>An anchor target macro (the destination for the links above).</summary>
    public static string AnchorTarget(string name) =>
        $"<ac:structured-macro ac:name=\"anchor\"><ac:parameter ac:name=\"\">{Esc(name)}</ac:parameter></ac:structured-macro>";

    /// <summary>Link to an uploaded attachment on this page. Cleaner reads ri:filename.</summary>
    public static string AttachmentLink(string fileName, string text) =>
        $"<ac:link><ri:attachment ri:filename=\"{Esc(fileName)}\"/>" +
        $"<ac:plain-text-link-body>{Cdata(text)}</ac:plain-text-link-body></ac:link>";

    /// <summary>Absolute external link.</summary>
    public static string ExternalLink(string href, string text) =>
        $"<a href=\"{Esc(href)}\">{Esc(text)}</a>";

    /// <summary>Root-relative link (e.g. <c>/spaces/KEY/pages/...</c>) — the body-view endpoint absolutizes these.</summary>
    public static string RootRelativeLink(string path, string text) =>
        $"<a href=\"{Esc(path)}\">{Esc(text)}</a>";

    /// <summary>A <c>mailto:</c> link.</summary>
    public static string MailLink(string address) => $"<a href=\"mailto:{Esc(address)}\">{Esc(address)}</a>";

    /// <summary>User mention. <c>ri:user</c> is dropped by the cleaner (opaque id) but is a real body construct.</summary>
    public static string UserMention(string userKey) =>
        $"<ac:link><ri:user ri:userkey=\"{Esc(userKey)}\"/></ac:link>";

    // ---- Images ----

    public static string ImageAttachment(string fileName, int? height = null, string? alt = null)
    {
        string attrs = (height is null ? string.Empty : $" ac:height=\"{height}\"") +
                       (alt is null ? string.Empty : $" ac:alt=\"{Esc(alt)}\"");
        return $"<ac:image{attrs}><ri:attachment ri:filename=\"{Esc(fileName)}\"/></ac:image>";
    }

    public static string ImageExternal(string url, int? width = null)
    {
        string attrs = width is null ? string.Empty : $" ac:width=\"{width}\"";
        return $"<ac:image{attrs}><ri:url ri:value=\"{Esc(url)}\"/></ac:image>";
    }

    // ---- Lists ----

    public static string BulletList(IEnumerable<string> itemsXhtml) =>
        "<ul>" + string.Concat(itemsXhtml.Select(i => $"<li>{i}</li>")) + "</ul>";

    public static string OrderedList(IEnumerable<string> itemsXhtml) =>
        "<ol>" + string.Concat(itemsXhtml.Select(i => $"<li>{i}</li>")) + "</ol>";

    /// <summary>A nested (two-level) bullet list.</summary>
    public static string NestedList(string parent, IEnumerable<string> children) =>
        $"<ul><li>{Esc(parent)}<ul>" + string.Concat(children.Select(c => $"<li>{Esc(c)}</li>")) + "</ul></li></ul>";

    public static string TaskList(IEnumerable<(bool Done, string Text)> tasks)
    {
        var sb = new StringBuilder("<ac:task-list>");
        int id = 1;
        foreach ((bool done, string text) in tasks)
        {
            sb.Append("<ac:task>")
                .Append($"<ac:task-id>{id++}</ac:task-id>")
                .Append($"<ac:task-status>{(done ? "complete" : "incomplete")}</ac:task-status>")
                .Append($"<ac:task-body>{Esc(text)}</ac:task-body>")
                .Append("</ac:task>");
        }

        return sb.Append("</ac:task-list>").ToString();
    }

    // ---- Tables ----

    public static string Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder("<table class=\"wrapped\"><tbody><tr>");
        foreach (string h in headers)
        {
            sb.Append($"<th>{Esc(h)}</th>");
        }

        sb.Append("</tr>");
        foreach (IReadOnlyList<string> row in rows)
        {
            sb.Append("<tr>");
            foreach (string cell in row)
            {
                // Cells may carry inline markup (links/code), so they're passed through as XHTML.
                sb.Append($"<td>{cell}</td>");
            }

            sb.Append("</tr>");
        }

        return sb.Append("</tbody></table>").ToString();
    }

    /// <summary>A table with merged cells (colspan/rowspan) — a distinct cleaner/render path.</summary>
    public static string MergedCellTable() =>
        "<table class=\"wrapped\"><tbody>" +
        "<tr><th>Environment</th><th>Safe Type</th><th>Result</th></tr>" +
        "<tr><td rowspan=\"2\">RND</td><td>STATIC-PROD</td><td>SUCCESS</td></tr>" +
        "<tr><td>DYNAMIC-RND</td><td>SUCCESS</td></tr>" +
        "<tr><td colspan=\"2\">PROD only retrieves from PROD</td><td>FAILURE</td></tr>" +
        "</tbody></table>";

    // ---- Macros ----

    public static string Code(string language, string code) =>
        "<ac:structured-macro ac:name=\"code\" ac:schema-version=\"1\">" +
        $"<ac:parameter ac:name=\"language\">{Esc(language)}</ac:parameter>" +
        $"<ac:plain-text-body>{Cdata(code)}</ac:plain-text-body></ac:structured-macro>";

    public static string NoFormat(string text) =>
        "<ac:structured-macro ac:name=\"noformat\" ac:schema-version=\"1\">" +
        $"<ac:plain-text-body>{Cdata(text)}</ac:plain-text-body></ac:structured-macro>";

    /// <summary>The <c>html</c> macro: its CDATA body is real HTML the cleaner re-tokenizes.</summary>
    public static string HtmlMacro(string rawHtml) =>
        "<ac:structured-macro ac:name=\"html\" ac:schema-version=\"1\">" +
        $"<ac:plain-text-body>{Cdata(rawHtml)}</ac:plain-text-body></ac:structured-macro>";

    /// <summary>info / note / warning / tip panels (rich-text body bubbles up to the index).</summary>
    public static string Panel(string kind, string? title, string innerXhtml)
    {
        string titleParam = title is null
            ? string.Empty
            : $"<ac:parameter ac:name=\"title\">{Esc(title)}</ac:parameter>";
        return $"<ac:structured-macro ac:name=\"{kind}\" ac:schema-version=\"1\">{titleParam}" +
               $"<ac:rich-text-body>{innerXhtml}</ac:rich-text-body></ac:structured-macro>";
    }

    public static string Expand(string title, string innerXhtml) =>
        "<ac:structured-macro ac:name=\"expand\" ac:schema-version=\"1\">" +
        $"<ac:parameter ac:name=\"title\">{Esc(title)}</ac:parameter>" +
        $"<ac:rich-text-body>{innerXhtml}</ac:rich-text-body></ac:structured-macro>";

    public static string Toc() =>
        "<ac:structured-macro ac:name=\"toc\" ac:schema-version=\"1\">" +
        "<ac:parameter ac:name=\"maxLevel\">3</ac:parameter></ac:structured-macro>";

    public static string Status(string colour, string text) =>
        "<ac:structured-macro ac:name=\"status\" ac:schema-version=\"1\">" +
        $"<ac:parameter ac:name=\"colour\">{Esc(colour)}</ac:parameter>" +
        $"<ac:parameter ac:name=\"title\">{Esc(text)}</ac:parameter></ac:structured-macro>";

    public static string Jira(string key) =>
        "<ac:structured-macro ac:name=\"jira\" ac:schema-version=\"1\">" +
        $"<ac:parameter ac:name=\"key\">{Esc(key)}</ac:parameter></ac:structured-macro>";

    public static string Emoticon(string name) => $"<ac:emoticon ac:name=\"{Esc(name)}\"/>";

    public static string Placeholder(string text) => $"<ac:placeholder>{Esc(text)}</ac:placeholder>";

    /// <summary>The Page Properties macro (a structured-macro wrapping a key/value table).</summary>
    public static string PageProperties(IEnumerable<(string Key, string Value)> pairs)
    {
        List<IReadOnlyList<string>> rows = pairs.Select(p => (IReadOnlyList<string>)new[] { Esc(p.Key), p.Value }).ToList();
        string table = Table(new[] { "Field", "Value" }, rows);
        return "<ac:structured-macro ac:name=\"details\" ac:schema-version=\"1\">" +
               $"<ac:rich-text-body>{table}</ac:rich-text-body></ac:structured-macro>";
    }
}
