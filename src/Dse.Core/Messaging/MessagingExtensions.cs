// Copyright (c) PNC Financial Services. All rights reserved.


using System.Security.Authentication;
using Dse.Data;
using Dse.Ingestion;
using Dse.Ingestion.Events;
using Dse.Shared;
using Humanizer;
using JasperFx;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.HealthChecks;
using Wolverine.Sqlite;
using SystemTextJsonSerializer = Wolverine.Runtime.Serialization.SystemTextJsonSerializer;

namespace Dse.Messaging;

public static class MessagingExtensions
{
    public static void AddMessaging(this IHostApplicationBuilder builder)
    {
        builder.Services.AddWolverine(opts =>
        {
            opts.DefaultSerializer = new SystemTextJsonSerializer(JsonDefaults.Web);

            opts.ServiceName = builder.Environment.ApplicationName;
            opts.ApplicationAssembly = typeof(MessagingExtensions).Assembly;

            // Single always-on pod: this node owns all durability work directly. Solo skips leader election and
            // distributed node-agent assignment — the machinery that, under Balanced, floods the log trying to hand
            // agents to ghost node rows. Switch to Balanced for true multi-node on the shared SQL Server store.
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;

            opts.PersistMessagesWithSqlite(builder.Configuration.GetSqliteConnectionString());
            opts.UseEntityFrameworkCoreTransactions();
            opts.PublishDomainEventsFromEntityFrameworkCore<IEntity>(x => x.Events);

            opts.Policies.AutoApplyTransactions();
            opts.Policies.UseDurableLocalQueues();
            opts.Policies.UseDurableInboxOnAllListeners();
            opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

            // Ingest execution: durable, and one run at a time so a source is never crawled by two runs at once.
            // The handler raises its own timeout past the 60s default; single-node today, exclusive-node when we
            // move to a shared SQL Server store.
            opts.LocalQueueFor<IngestRunCreatedEvent>().Sequential();

            opts.Policies
                .OnException<ConcurrencyException>()
                .RetryTimes(3)
                .Then
                .MoveToErrorQueue();

            opts.Policies
                .OnException<SqliteException>()
                .Or<TimeoutException>()
                .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds())
                .Then
                .MoveToErrorQueue();

            opts.Policies
                .OnException<InvalidOperationException>()
                .Requeue()
                .AndPauseProcessing(10.Minutes());

            opts.Policies
                .OnException<AuthenticationException>()
                .MoveToErrorQueue();
        });

        builder.Services
            .AddHealthChecks()
            .AddWolverine(tags: ["live", "ready"])
            .AddWolverineListeners(tags: ["ready"]);
    }
}
