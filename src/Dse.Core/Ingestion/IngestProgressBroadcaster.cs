// Copyright (c) PNC Financial Services. All rights reserved.


using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;

namespace Dse.Ingestion;

/// <summary>
///     A single transition published to live subscribers — mirrors a row of <see cref="IngestRunEvent" />
///     but carries the typed payload directly so consumers don't need to deserialize.
/// </summary>
public sealed record IngestProgressEvent(Guid RunId, long Seq, DateTimeOffset At, IngestEventPayload Payload);

/// <summary>
///     In-process pub-sub for live ingestion progress. The durable record is
///     <see cref="IngestRunEvent" /> in the database; this broadcaster gives SignalR / SSE consumers a
///     low-latency push channel without polling the DB.
/// </summary>
/// <remarks>
///     Late joiners should hydrate from the DB (<see cref="IngestRunEvent" /> ordered by
///     <see cref="IngestRunEvent.Seq" />) and then subscribe to live events whose <c>Seq</c> exceeds the
///     last DB row they read. The broadcaster intentionally does not buffer history.
/// </remarks>
public sealed class IngestProgressBroadcaster
{
    private readonly ConcurrentDictionary<Guid, ImmutableHashSet<Channel<IngestProgressEvent>>> _subscribers = new();

    /// <summary>Publish <paramref name="evt" /> to every subscriber for <paramref name="evt" />.RunId.</summary>
    /// <remarks>
    ///     Subscribers' channels are unbounded; a writer that never reads only costs memory until the
    ///     subscription is disposed. If a slow consumer becomes a problem, switch the channel to bounded
    ///     with <see cref="BoundedChannelFullMode.DropOldest" />.
    /// </remarks>
    public void Publish(IngestProgressEvent evt)
    {
        if (!_subscribers.TryGetValue(evt.RunId, out ImmutableHashSet<Channel<IngestProgressEvent>>? set))
        {
            return;
        }

        foreach (Channel<IngestProgressEvent> ch in set)
        {
            // Unbounded TryWrite never returns false unless completed; ignore the result either way.
            ch.Writer.TryWrite(evt);
        }
    }

    /// <summary>
    ///     Subscribe to live progress for <paramref name="runId" />. Dispose the returned subscription to
    ///     stop receiving events.
    /// </summary>
    public IIngestProgressSubscription Subscribe(Guid runId)
    {
        var channel = Channel.CreateUnbounded<IngestProgressEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        _subscribers.AddOrUpdate(
            runId,
            _ => [channel],
            (_, set) => set.Add(channel));

        return new Subscription(this, runId, channel);
    }

    private void Unsubscribe(Guid runId, Channel<IngestProgressEvent> channel)
    {
        _subscribers.AddOrUpdate(
            runId,
            _ => [],
            (_, set) => set.Remove(channel));
        channel.Writer.TryComplete();
    }

    private sealed class Subscription(
        IngestProgressBroadcaster owner,
        Guid runId,
        Channel<IngestProgressEvent> channel) : IIngestProgressSubscription
    {
        public ChannelReader<IngestProgressEvent> Reader => channel.Reader;

        public ValueTask DisposeAsync()
        {
            owner.Unsubscribe(runId, channel);
            return ValueTask.CompletedTask;
        }
    }
}

public interface IIngestProgressSubscription : IAsyncDisposable
{
    ChannelReader<IngestProgressEvent> Reader { get; }
}
