using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MeTube.Search;

/// <summary>
/// The sort and filter choices YouTube's search still honours (checked live in
/// October 2026), whitelisted so nothing user-supplied reaches YouTube verbatim.
/// YouTube dropped "upload date" and "rating" sorts (they are silently ignored), and
/// it never sorted by length; the client reorders loaded results for those.
/// </summary>
public sealed record SearchFilters(string Sort, string Duration, string Date)
{
    /// <summary>Gets the default: relevance, any length, any date.</summary>
    public static SearchFilters Default { get; } = new("relevance", "any", "any");

    private static readonly Dictionary<string, byte> SortCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["relevance"] = 0,
        ["popularity"] = 3
    };

    // YouTube's current length buckets: under 3 minutes, 3-20 minutes, over 20 minutes.
    private static readonly Dictionary<string, byte> DurationCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["any"] = 0,
        ["short"] = 4,
        ["medium"] = 5,
        ["long"] = 2
    };

    // Rolling windows: "year" means the last 12 months, not the calendar year.
    private static readonly Dictionary<string, byte> DateCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["any"] = 0,
        ["today"] = 2,
        ["week"] = 3,
        ["month"] = 4,
        ["year"] = 5
    };

    /// <summary>Gets a value indicating whether nothing beyond the default is selected.</summary>
    public bool IsDefault => this == Default;

    /// <summary>Gets a stable key for caching.</summary>
    public string CacheKey => Sort + "/" + Duration + "/" + Date;

    /// <summary>Maps query-string values onto the whitelist; anything unknown becomes the default.</summary>
    public static SearchFilters Parse(string? sort, string? duration, string? date) => new(
        Normalize(sort, SortCodes, "relevance"),
        Normalize(duration, DurationCodes, "any"),
        Normalize(date, DateCodes, "any"));

    /// <summary>
    /// Encodes the InnerTube "params" protobuf: field 1 = sort, field 2 = filters
    /// { 1: upload date, 2: type (1 = video), 3: duration }, and field 30 = 1, which
    /// keeps YouTube from replacing results with a crisis-help screen for queries
    /// such as song titles that mention self-harm. Every value is below 128, so each
    /// varint and length is a single byte.
    /// </summary>
    public string ToParams()
    {
        var filters = new List<byte>();
        if (DateCodes[Date] != 0)
        {
            filters.Add(0x08);
            filters.Add(DateCodes[Date]);
        }

        filters.Add(0x10);
        filters.Add(0x01);
        if (DurationCodes[Duration] != 0)
        {
            filters.Add(0x18);
            filters.Add(DurationCodes[Duration]);
        }

        var bytes = new List<byte>();
        if (SortCodes[Sort] != 0)
        {
            bytes.Add(0x08);
            bytes.Add(SortCodes[Sort]);
        }

        bytes.Add(0x12);
        bytes.Add((byte)filters.Count);
        bytes.AddRange(filters);
        bytes.Add(0xF0);
        bytes.Add(0x01);
        bytes.Add(0x01);
        return Convert.ToBase64String(bytes.ToArray());
    }

    private static string Normalize(string? value, Dictionary<string, byte> codes, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value) || !codes.ContainsKey(value.Trim()))
        {
            return fallback;
        }

        return value.Trim().ToLowerInvariant();
    }
}
