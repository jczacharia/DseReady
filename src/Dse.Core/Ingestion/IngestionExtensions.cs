// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dse.Ingestion;

[ExcludeFromCodeCoverage]
public static class IngestionExtensions
{
    public static void AddIngestion(this IHostApplicationBuilder builder) =>
        // Process-wide registry of in-flight runs (live snapshot + cancellation).
        builder.Services.AddSingleton<IIngestRunControl, IngestRunControl>();
}
