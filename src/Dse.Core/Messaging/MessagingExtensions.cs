// Copyright (c) PNC Financial Services. All rights reserved.


using System.Security.Authentication;
using Dse.Data;
using Dse.Shared;
using Humanizer;
using JasperFx;
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
            opts.Durability.Mode = DurabilityMode.Balanced; // F5 Load balanced
            opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
            opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;

            opts.PersistMessagesWithSqlite(builder.Configuration.GetSqliteConnectionString());
            opts.UseEntityFrameworkCoreTransactions();
            opts.UseEntityFrameworkCoreWolverineManagedMigrations();

            opts.Policies.AutoApplyTransactions();
            opts.Policies.UseDurableLocalQueues();
            opts.Policies.UseDurableInboxOnAllListeners();
            opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

            opts.Policies
                .OnException<ConcurrencyException>()
                .RetryTimes(3)
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

        builder.Services.AddHealthChecks()
            .AddWolverine(tags: ["live", "ready"])
            .AddWolverineListeners(tags: ["ready"]);
    }
}
