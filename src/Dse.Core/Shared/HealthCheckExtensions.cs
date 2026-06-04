// Copyright (c) PNC Financial Services. All rights reserved.


using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Dse.Shared;

public sealed record DseHealthReport(
    string Status,
    string TotalDuration,
    IEnumerable<HealthReportEntry> Checks);

public sealed record HealthReportEntry(
    string Name,
    string Status,
    string Duration,
    string? Description,
    string? Exception,
    IReadOnlyDictionary<string, object> Data);

public static class HealthCheckExtensions
{
    public static HealthCheckOptions WithReportWriter(this HealthCheckOptions options)
    {
        options.ResponseWriter = WriteHealthReport;
        return options;
    }

    private static async Task WriteHealthReport(HttpContext context, HealthReport report)
    {
        string result = JsonSerializer.Serialize(ToReport(report), JsonDefaults.Pretty);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(result);
    }

    private static DseHealthReport ToReport(HealthReport report) => new(
        report.Status.ToString(),
        report.TotalDuration.ToString(),
        report.Entries.Select(e => new HealthReportEntry(e.Key,
            e.Value.Status.ToString(),
            e.Value.Duration.ToString(),
            e.Value.Description,
            e.Value.Exception?.Message,
            e.Value.Data)));
}
