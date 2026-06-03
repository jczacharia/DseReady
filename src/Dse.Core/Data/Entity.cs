// Copyright (c) PNC Financial Services. All rights reserved.


using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Dse.Data;

public interface IEntity
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset? UpdatedAt { get; set; }
}

public interface IEntity<TKey> : IEntity where TKey : notnull
{
    TKey Id { get; init; }
}

public abstract class Entity<TKey> : IEntity<TKey> where TKey : notnull
{
    public required TKey Id { get; init; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public abstract class Entity : IEntity<Guid>
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed record EntityResponse<TKey>(TKey Id, Uri Location) where TKey : notnull;

public static class EntityExtensions
{
    public static AcceptedAtRoute<EntityResponse<TKey>> EntityAccepted<TKey, TEntity>(
        this HttpContext httpContext,
        TEntity entity,
        string routeName,
        object? routeValues) where TEntity : IEntity<TKey> where TKey : notnull
    {
        var linkGenerator = httpContext.RequestServices.GetRequiredService<LinkGenerator>();
        string? location = linkGenerator.GetUriByName(httpContext, routeName, routeValues);
        var uri = new Uri(location ?? throw new NotSupportedException($"Could not create URI for endpoint '{routeName}'"));
        return TypedResults.AcceptedAtRoute(new EntityResponse<TKey>(entity.Id, uri), routeName, routeValues);
    }
}
