// Copyright (c) PNC Financial Services. All rights reserved.


using System.Globalization;
using System.Text;
using System.Web;

namespace Dse.Tests.Ingestion;

/// <summary>
///     Per-test control surface for <see cref="StubConfluenceHandler" />. The Alba host is built once and shared,
///     so the stub can't be swapped between tests — instead a test steers this singleton: how many documents the
///     fake corpus holds, and (for the cancel test) a gate that parks the document-fetch mid-flight so the run is
///     provably in-flight when cancellation arrives.
/// </summary>
public sealed class StubConfluenceState
{
    private TaskCompletionSource _entered = NewTcs();
    private TaskCompletionSource _release = NewTcs();

    /// <summary>Size of the fake corpus — the <c>totalSize</c> a search reports and the docs a page can yield.</summary>
    public int Total { get; set; }

    /// <summary>When set, the page-fetch parks on <see cref="ReleaseTask" /> until released or canceled.</summary>
    public bool GatePageFetch { get; private set; }

    /// <summary>Completes the first time the handler serves a document page — the run is now mid-ingest.</summary>
    public Task PageFetchEntered => _entered.Task;

    internal Task ReleaseTask => _release.Task;

    /// <summary>Arms the gate with fresh signals for one cancel scenario.</summary>
    public void BeginGate()
    {
        _entered = NewTcs();
        _release = NewTcs();
        GatePageFetch = true;
    }

    public void Release() => _release.TrySetResult();

    internal void SignalEntered() => _entered.TrySetResult();

    public void Reset()
    {
        GatePageFetch = false;
        Total = 0;
        _entered = NewTcs();
        _release = NewTcs();
    }

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
///     Stubs the Confluence REST API at the <see cref="HttpMessageHandler" /> boundary — the one seam CI can't
///     reach (it has Elasticsearch but no Confluence). Everything above it runs for real: the named "confluence"
///     client and its resilience pipeline, <c>ConfluenceIngest</c>'s URL/CQL building and JSON parsing, the HTML
///     cleaner, and the live <c>IngestRunner&lt;ConfluenceDoc&gt;</c> writing to a real Elasticsearch. It honors
///     true pagination semantics (<c>start</c>/<c>limit</c>) so a dry run fetches exactly one document.
/// </summary>
internal sealed class StubConfluenceHandler(StubConfluenceState state) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var query = HttpUtility.ParseQueryString(request.RequestUri?.Query ?? string.Empty);
        int start = int.Parse(query["start"] ?? "0", CultureInfo.InvariantCulture);
        int limit = int.Parse(query["limit"] ?? "0", CultureInfo.InvariantCulture);

        // The count probe (limit=0) only reads totalSize; never gated, never returns documents.
        if (limit == 0)
        {
            return Json($$"""{"results":[],"totalSize":{{state.Total}}}""");
        }

        state.SignalEntered();
        if (state.GatePageFetch)
        {
            // Park until the test releases — or until the runner's cancellation token trips, which is exactly
            // the cooperative-cancel path the cancel endpoint exercises.
            await state.ReleaseTask.WaitAsync(ct).ConfigureAwait(false);
        }

        int count = Math.Clamp(state.Total - start, 0, limit);
        string results = string.Join(',', Enumerable.Range(start, count).Select(Document));
        return Json($$"""{"results":[{{results}}],"totalSize":{{state.Total}}}""");
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    // A complete-enough Confluence content object: every field ConfluenceDocJsonConverter navigates, plus body
    // markup with a macro so the real ConfluenceHtmlCleaner runs over it during ingestion.
    private static string Document(int i) => $$"""
        {
          "id": "{{i + 1}}",
          "type": "page",
          "title": "Stub Page {{i + 1}}",
          "body": { "storage": { "value": "<p>Document {{i + 1}} body.</p><ac:structured-macro ac:name=\"info\"><ac:rich-text-body><p>note {{i + 1}}</p></ac:rich-text-body></ac:structured-macro>" } },
          "space": { "id": 100, "key": "DEV", "name": "Developers", "_links": { "webui": "/display/DEV" } },
          "version": { "number": {{i + 1}}, "when": "2026-01-0{{(i % 9) + 1}}T00:00:00Z", "by": { "username": "author{{i}}", "userKey": "key{{i}}", "displayName": "Author {{i}}", "profilePicture": { "path": "/images/{{i}}.png" } } },
          "history": { "createdDate": "2025-01-0{{(i % 9) + 1}}T00:00:00Z", "createdBy": { "username": "creator{{i}}", "userKey": "ckey{{i}}", "displayName": "Creator {{i}}", "profilePicture": { "path": "/images/c{{i}}.png" } } },
          "ancestors": [ { "id": "0", "type": "page", "title": "Home", "_links": { "webui": "/display/DEV/Home" } } ],
          "metadata": { "labels": { "results": [ { "id": "lbl{{i}}", "name": "stub", "prefix": "global" } ] } }
        }
        """;
}
