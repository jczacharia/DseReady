// Copyright (c) PNC Financial Services. All rights reserved.


using System.Text.Json;
using Dse.Shared;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Polly.Timeout;

namespace Dse.Sources.Confluence;

/// <summary>
///     Returns a Confluence page's rendered body as HTML for read-through display. Every anchor is rewritten to an
///     absolute Confluence URL (resolved against the page's own URL from the response <c>_links</c>) and opened in a
///     new tab, so a click always leaves this view for Confluence proper — where the user's session handles auth.
///     We deliberately do NOT proxy assets (images, etc.): serving Confluence-hosted binaries through this app is a
///     PII exposure risk. Images that need a session simply won't render here; the user clicks through to see them.
/// </summary>
public sealed class ConfluenceBodyViewEndpoint : IEndpoint
{
    // Only the rendered representations — never storage/editor (internal formats, not for display). export_view
    // already absolutizes internal links; view leaves them relative. Either way we absolutize below.
    private static readonly HashSet<string> s_allowedRepresentations =
        new(StringComparer.Ordinal) { "export_view", "anonymous_export_view", "view" };

    public void MapEndpoint(IEndpointRouteBuilder builder) => builder
        .MapGet("sources/confluence/content/{contentId}/body-view", async Task<IResult> (
            string contentId,
            [FromQuery] string? bodyExport,
            IHttpClientFactory httpClientFactory,
            CancellationToken ct) =>
        {
            // Confluence content ids are numeric — reject anything else so it can't be injected into the upstream path.
            if (!long.TryParse(contentId, out _))
            {
                return Results.BadRequest($"Invalid content id '{contentId}'.");
            }

            string representation = bodyExport ?? "export_view";
            if (!s_allowedRepresentations.Contains(representation))
            {
                return Results.BadRequest(
                    $"Unsupported body representation '{representation}'. Allowed: {string.Join(", ", s_allowedRepresentations)}.");
            }

            // Read-through client: its resilience pipeline owns the timeout, so no hand-rolled cancellation here.
            HttpClient client = httpClientFactory.CreateClient(ConfluenceModule.ReadThroughClient);

            try
            {
                using HttpResponseMessage upstream = await client.GetAsync(
                    $"rest/api/content/{contentId}?expand=body.{representation}", ct);

                if (!upstream.IsSuccessStatusCode)
                {
                    // Pass Confluence's status + body through verbatim (404 unknown id, 401/403 auth, etc.).
                    string error = await upstream.Content.ReadAsStringAsync(ct);
                    return Results.Content(error, upstream.Content.Headers.ContentType?.ToString() ?? "application/json",
                        statusCode: (int)upstream.StatusCode);
                }

                await using Stream payload = await upstream.Content.ReadAsStreamAsync(ct);
                using JsonDocument json = await JsonDocument.ParseAsync(payload, cancellationToken: ct);
                JsonElement root = json.RootElement;

                // The rendered HTML lives in the JSON envelope at body.{representation}.value.
                if (!root.TryGetProperty("body", out JsonElement body)
                    || !body.TryGetProperty(representation, out JsonElement view)
                    || !view.TryGetProperty("value", out JsonElement value)
                    || value.GetString() is not { } rawHtml)
                {
                    return Results.Problem(
                        $"Confluence response did not contain body.{representation}.value for content '{contentId}'.",
                        statusCode: StatusCodes.Status502BadGateway);
                }

                Uri pageUrl = ResolvePageUrl(root, contentId);
                return Results.Content(AbsolutizeLinks(rawHtml, pageUrl), "text/html; charset=utf-8");
            }
            catch (TimeoutRejectedException)
            {
                return Results.Problem("Confluence did not respond in time.",
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
        });

    /// <summary>
    ///     The page's absolute Confluence URL, built from the response <c>_links</c> (<c>base [+ context] + webui</c>)
    ///     — the actual page address, so relative links and <c>#fragments</c> resolve correctly. Falls back to the
    ///     stable <c>viewpage.action?pageId=</c> form if <c>webui</c> is absent.
    /// </summary>
    internal static Uri ResolvePageUrl(JsonElement root, string contentId)
    {
        string? @base = null, context = null, webui = null;
        if (root.TryGetProperty("_links", out JsonElement links))
        {
            @base = links.TryGetProperty("base", out JsonElement b) ? b.GetString() : null;
            context = links.TryGetProperty("context", out JsonElement c) ? c.GetString() : null;
            webui = links.TryGetProperty("webui", out JsonElement w) ? w.GetString() : null;
        }

        if (@base is null || !Uri.TryCreate(@base, UriKind.Absolute, out Uri? baseUri))
        {
            throw new InvalidOperationException("Confluence response did not include a usable '_links.base'.");
        }

        string prefix = context ?? string.Empty;
        string relative = !string.IsNullOrEmpty(webui) ? prefix + webui : $"{prefix}/pages/viewpage.action?pageId={contentId}";
        return new Uri(baseUri, relative);
    }

    /// <summary>
    ///     Rewrites every <c>&lt;a href&gt;</c> to an absolute URL resolved against the page's Confluence URL, so the
    ///     view never contains a relative link: relative paths, root-relative paths, and <c>#fragments</c> all become
    ///     absolute Confluence URLs, while already-absolute links (external sites, <c>mailto:</c>, the page itself)
    ///     keep pointing where they point. Anchors open in a new tab so a click leaves the read-through view.
    /// </summary>
    internal static string AbsolutizeLinks(string html, Uri pageUrl)
    {
        HtmlDocument document = new();
        document.LoadHtml(html);

        if (document.DocumentNode.SelectNodes("//a[@href]") is not { } anchors)
        {
            return html;
        }

        foreach (HtmlNode anchor in anchors)
        {
            string href = anchor.GetAttributeValue("href", string.Empty);
            if (href.Length == 0 || !Uri.TryCreate(pageUrl, href, out Uri? absolute))
            {
                continue;
            }

            anchor.SetAttributeValue("href", absolute.ToString());
            anchor.SetAttributeValue("target", "_blank");
            anchor.SetAttributeValue("rel", "noopener noreferrer");
        }

        return document.DocumentNode.OuterHtml;
    }
}
