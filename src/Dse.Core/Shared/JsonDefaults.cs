// Copyright (c) PNC Financial Services. All rights reserved.


using System.Text.Json;
using Thinktecture.Text.Json.Serialization;

namespace Dse.Shared;

public static class JsonDefaults
{
    public static readonly ThinktectureJsonConverterFactory Thinktecture = new();

    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { Thinktecture },
    };

    public static readonly JsonSerializerOptions Pretty = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { Thinktecture },
    };
}
