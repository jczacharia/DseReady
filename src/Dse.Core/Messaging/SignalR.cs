// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.SignalR;

namespace Dse.Messaging;

[ExcludeFromCodeCoverage]
public sealed class MessagingHub : Hub;

[ExcludeFromCodeCoverage]
public sealed record SignalREnvelope(object Payload, string[] UserIds)
{
    public string Type => Payload.GetType().Name;
}

[ExcludeFromCodeCoverage]
public static class SignalRMessageHandler
{
    public static async Task Handle(SignalREnvelope envelope, IHubContext<MessagingHub> hubContext) =>
        await hubContext.Clients.Users(envelope.UserIds).SendAsync("ReceiveMessage", envelope.Payload);
}
