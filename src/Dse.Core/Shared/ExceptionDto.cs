// Copyright (c) PNC Financial Services. All rights reserved.


using System.Collections;
using System.Collections.ObjectModel;

namespace Dse.Shared;

public sealed record ExceptionDto(
    string Type,
    string Message,
    string? StackTrace,
    string? Source,
    int HResult,
    IReadOnlyDictionary<string, string?>? Data,
    ExceptionDto? InnerException,
    IReadOnlyList<ExceptionDto>? InnerExceptions)
{
    public static ExceptionDto From(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return new ExceptionDto(
            ex.GetType().FullName ?? ex.GetType().Name,
            ex.Message,
            ex.StackTrace,
            ex.Source,
            ex.HResult,
            ex.Data.Count > 0 ? CaptureData(ex.Data) : null,
            ex.InnerException is { } inner ? From(inner) : null,
            ex is AggregateException agg
                ? agg.InnerExceptions.Select(From).ToArray()
                : null);
    }

    private static ReadOnlyDictionary<string, string?> CaptureData(IDictionary data)
    {
        Dictionary<string, string?> dict = new(data.Count);
        foreach (DictionaryEntry entry in data)
        {
            if (entry.Key is string key)
            {
                dict[key] = entry.Value?.ToString();
            }
        }

        return dict.AsReadOnly();
    }
}
