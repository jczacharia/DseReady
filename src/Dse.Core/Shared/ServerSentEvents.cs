// Copyright (c) PNC Financial Services. All rights reserved.


using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Dse.Shared;

/// <summary>
///     Streams an <see cref="IAsyncEnumerable{T}" /> to the client as Server-Sent Events. Unlike returning the
///     sequence directly (which the framework buffers into a single JSON array and only flushes at the end),
///     this writes one <c>data:</c> frame per item and flushes immediately, so a browser sees each event as it
///     is produced. Each item is serialized with the application's configured HTTP JSON options, so polymorphic
///     discriminators and custom converters apply exactly as they do for normal responses.
/// </summary>
public static class ServerSentEvents
{
    public static IResult Stream<T>(IAsyncEnumerable<T> events) => new SseResult<T>(events);

    private sealed class SseResult<T>(IAsyncEnumerable<T> events) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            CancellationToken ct = httpContext.RequestAborted;
            HttpResponse response = httpContext.Response;

            response.Headers.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache,no-store";
            response.Headers.Pragma = "no-cache";
            // Defeats proxy buffering (e.g. nginx) that would otherwise hold events back.
            response.Headers["X-Accel-Buffering"] = "no";

            // Kestrel buffers the response body by default; turn it off so each flush hits the wire.
            httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            // Commit the status line and headers so the client opens the stream before the first event.
            await response.StartAsync(ct);

            try
            {
                await foreach (T evt in events.WithCancellation(ct))
                {
                    // Web (non-indented) options keep each payload on a single line, which SSE framing requires.
                    string payload = JsonSerializer.Serialize(evt, JsonDefaults.Web);
                    await response.WriteAsync($"data: {payload}\n\n", ct);
                    await response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Client navigated away / closed the tab — a normal end to a stream, not an error.
            }
        }
    }
}
