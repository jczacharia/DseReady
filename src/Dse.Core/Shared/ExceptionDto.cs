// Copyright (c) PNC Financial Services. All rights reserved.


namespace Dse.Shared;

public sealed class ExceptionDto
{
    public static ExceptionDto From(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return new ExceptionDto
        {
            Type = ex.GetType().FullName ?? ex.GetType().Name,
            Message = ex.Message,
            StackTrace = ex.StackTrace,
            Source = ex.Source,
            HResult = ex.HResult,
        };
    }

    public required string Type { get; init; }
    public required string Message { get; init; }
    public required string? StackTrace { get; init; }
    public required string? Source { get; init; }
    public required int HResult { get; init; }
}
