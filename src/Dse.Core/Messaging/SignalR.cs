// Copyright (c) PNC Financial Services. All rights reserved.


using Microsoft.AspNetCore.SignalR;

namespace Dse.Messaging;

public sealed class MessagingHub : Hub;

public sealed record SignalREnvelope(object Payload, string[] UserIds)
{
    public string Type => Payload.GetType().Name;
}

public static class SignalRMessageHandler
{
    public static async Task Handle(SignalREnvelope envelope, IHubContext<MessagingHub> hubContext) =>
        await hubContext.Clients.Users(envelope.UserIds).SendAsync("ReceiveMessage", envelope.Payload);
}
