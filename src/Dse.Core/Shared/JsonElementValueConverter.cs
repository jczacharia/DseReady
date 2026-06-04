// Copyright (c) PNC Financial Services. All rights reserved.


using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Dse.Shared;

public sealed class JsonDocumentValueConverter() : ValueConverter<JsonDocument, string>(
    doc => doc.RootElement.GetRawText(),
    json => JsonDocument.Parse(json, s_docOpts))
{
    private static readonly JsonDocumentOptions s_docOpts = new() { MaxDepth = 64, };
}
