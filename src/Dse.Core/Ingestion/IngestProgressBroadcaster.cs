// Copyright (c) PNC Financial Services. All rights reserved.


using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;

namespace Dse.Ingestion;

public sealed record IngestProgressEvent(Guid RunId, long Seq, DateTimeOffset At, IngestEventPayload Payload);

/// <summary>In-process pub-sub for live ingestion progress; history lives in <see cref="IngestRunEvent" />.</summary>
public sealed class IngestProgressBroadcaster
{
    private readonly ConcurrentDictionary<Guid, ImmutableHashSet<Channel<IngestProgressEvent>>> _subscribers = new();

    public void Publish(IngestProgressEvent evt)
    {
        if (!_subscribers.TryGetValue(evt.RunId, out ImmutableHashSet<Channel<IngestProgressEvent>>? set))
        {
            return;
        }

        foreach (Channel<IngestProgressEvent> ch in set)
        {
            ch.Writer.TryWrite(evt);
        }
    }

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
