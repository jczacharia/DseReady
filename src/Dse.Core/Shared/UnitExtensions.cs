// Copyright (c) PNC Financial Services. All rights reserved.


using Humanizer;
using UnitsNet;

namespace Dse.Shared;

public static class UnitExtensions
{
    /// <summary>
    ///     Formats an <see cref="Information" /> value using the largest whole unit
    ///     (e.g. 117.74 MB) via Humanizer's <see cref="ByteSize" />.
    /// </summary>
    /// <remarks>
    ///     Uses decimal prefixes (1 KB = 1000 B), not binary (1 KiB = 1024 B).
    /// </remarks>
    public static string Humanize(this Information info) => ByteSize.FromBytes((double)info.Bytes).ToString();
}
