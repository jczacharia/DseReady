// Copyright (c) PNC Financial Services. All rights reserved.


using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Dse.Sources.Confluence;

[ExcludeFromCodeCoverage]
public sealed class ConfluenceOptions
{
    public const string SectionName = "Confluence";

    public string BaseAddress { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? Proxy { get; set; }

    /// <summary>
    ///     Confluence caps at 50 content search endpoint when body is requested so do not go over this limit.
    /// </summary>
    [Range(minimum: 1, maximum: 50)]
    public int PageSize { get; set; } = 50;

    public int InboundBufferMaxSize { get; set; } = 5000;
    public int OutboundBufferMaxSize { get; set; } = 250;

    public string ContentCql { get; set; } = "type in (page,blogpost) order by lastModified desc";

    public string[] ContentExpand { get; set; } =
    [
        "ancestors",
        "body.storage",
        "history",
        "metadata.labels",
        "space",
        "version",
    ];
}
