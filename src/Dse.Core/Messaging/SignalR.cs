// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Shared;
using Microsoft.AspNetCore.SignalR;

namespace Dse.Messaging;

public sealed class MessagingHub : Hub;

public sealed record SignalREnvelope(object Payload, string[] UserIds)
{
    public string Type => Payload.GetType().Name;
}

public abstract record SignalRMessage
{
    public string Type => GetType().Name;
}

public sealed record CaseAssetReportUpdated(Guid CaseId, string AssetReportId) : SignalRMessage;

public static class SignalRMessageHandler
{
    public static async Task Handle(SignalREnvelope envelope, IHubContext<MessagingHub> hubContext) =>
        await hubContext.Clients.Users(envelope.UserIds).SendAsync("ReceiveMessage", envelope.Payload);
}
