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
}

public abstract class Entity<TId> : IEquatable<Entity<TId>>, IEntity where TId : notnull
{
    protected Entity() { } // Required for ORM hydration

    protected Entity(TId id) => Id = id;

    // default! required for ORM hydration; Id is set immediately after construction
    public TId Id { get; protected set; } = default!;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public bool Equals(Entity<TId>? other) =>
        other is not null
        && GetType() == other.GetType()
        && EqualityComparer<TId>.Default.Equals(Id, other.Id);

    public override bool Equals(object? obj) => obj is Entity<TId> other && Equals(other);

    public override int GetHashCode() => EqualityComparer<TId>.Default.GetHashCode(Id);
    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) => Equals(left, right);
    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !Equals(left, right);
}

public interface IAggregateRoot : IEntity
{
    IReadOnlyList<IDomainEvent> DomainEvents { get; }
}

public abstract class AggregateRoot<TId> : Entity<TId>, IAggregateRoot where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot() { }
    protected AggregateRoot(TId id) : base(id) { }
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void RaiseDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
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
