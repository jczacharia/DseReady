// Copyright (c) PNC Financial Services. All rights reserved.


using Thinktecture;

namespace Dse.Shared;

[Union]
public abstract partial record Result<TValue, TError>
{
    public sealed record Success(TValue Value) : Result<TValue, TError>;

    public sealed record Failure(TError Error) : Result<TValue, TError>;
}

[Union]
public abstract partial record ResultValue<TValue>
{
    public sealed record Success(TValue Value) : ResultValue<TValue>;

    public sealed record Failure : ResultValue<TValue>;
}

[Union]
public abstract partial record ResultError<TError>
{
    public sealed record Success : ResultError<TError>;

    public sealed record Failure(TError Error) : ResultError<TError>;
}
