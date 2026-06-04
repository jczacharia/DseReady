// Copyright (c) PNC Financial Services. All rights reserved.


using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Dse.Data;

public interface IDomainEvent;

public interface IEntity
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset? UpdatedAt { get; set; }
    IReadOnlyList<IDomainEvent> Events { get; }
    public void Publish(IDomainEvent @event);
}

public interface IEntity<TKey> : IEntity where TKey : notnull
{
    TKey Id { get; init; }
}

public abstract class Entity<TKey> : IEntity<TKey> where TKey : notnull
{
    public abstract TKey Id { get; init; }
    public virtual DateTimeOffset CreatedAt { get; set; }
    public virtual DateTimeOffset? UpdatedAt { get; set; }

    private readonly List<object> _events = [];
    IReadOnlyList<IDomainEvent> IEntity.Events => _events.OfType<IDomainEvent>().ToList();

    public void Publish(IDomainEvent e)
    {
        _events.Add(e);
    }
}

public sealed record EntityResponse<TKey>(TKey Id, Uri Location) where TKey : notnull;

public static class EntityExtensions
{
    public static AcceptedAtRoute<EntityResponse<TKey>> EntityAccepted<TKey>(
        this HttpContext httpContext,
        TKey id,
        string routeName,
        object? routeValues) where TKey : notnull
    {
        var linkGenerator = httpContext.RequestServices.GetRequiredService<LinkGenerator>();
        string? location = linkGenerator.GetUriByName(httpContext, routeName, routeValues);
        var uri = new Uri(location ?? throw new NotSupportedException($"Could not create URI for endpoint '{routeName}'"));
        return TypedResults.AcceptedAtRoute(new EntityResponse<TKey>(id, uri), routeName, routeValues);
    }
}
