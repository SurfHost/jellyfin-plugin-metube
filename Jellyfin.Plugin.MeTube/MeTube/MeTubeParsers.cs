using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.MeTube.Models;
using Jellyfin.Plugin.MeTube.Search;

namespace Jellyfin.Plugin.MeTube.MeTube;

/// <summary>Parses MeTube API responses. Pure functions, tested against captured responses.</summary>
public static class MeTubeParsers
{
    /// <summary>Parses GET /history: { done: [...], queue: [...], pending: [...] }.</summary>
    public static MeTubeHistory ParseHistory(JsonElement root)
    {
        var history = new MeTubeHistory();
        ReadEntries(root, "done", history.Done);
        ReadEntries(root, "queue", history.Queue);
        ReadEntries(root, "pending", history.Pending);
        return history;
    }

    /// <summary>
    /// Interprets the answer to POST /add. MeTube replies HTTP 400 with plain text for
    /// invalid options or an unknown preset, and HTTP 200 with
    /// { status: "ok" | "error", msg } otherwise, including "ok" for a URL that was
    /// already queued.
    /// </summary>
    public static MeTubeAddResult ParseAddResponse(int statusCode, string body)
    {
        if (statusCode == 400)
        {
            var text = string.IsNullOrWhiteSpace(body) ? "MeTube rejected the request." : body.Trim();
            return new MeTubeAddResult(MeTubeAddOutcome.Error, Truncate(text, 300), ConfigProblem: true);
        }

        if (statusCode < 200 || statusCode > 299)
        {
            return new MeTubeAddResult(MeTubeAddOutcome.Error, "MeTube returned HTTP " + statusCode.ToString(CultureInfo.InvariantCulture) + ".", ConfigProblem: true);
        }

        string? status = null;
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            status = GetString(document.RootElement, "status");
            message = GetString(document.RootElement, "msg");
        }
        catch (JsonException)
        {
            return new MeTubeAddResult(MeTubeAddOutcome.Error, "MeTube returned an unexpected answer.", ConfigProblem: true);
        }

        if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return message is not null && message.Contains("already", StringComparison.OrdinalIgnoreCase)
                ? new MeTubeAddResult(MeTubeAddOutcome.AlreadyQueued, message)
                : new MeTubeAddResult(MeTubeAddOutcome.Ok, message);
        }

        return new MeTubeAddResult(MeTubeAddOutcome.Error, Truncate(message ?? "MeTube could not add the video.", 300));
    }

    /// <summary>Parses GET /presets. Accepts a list of names or a list of { name } objects.</summary>
    public static IReadOnlyList<string> ParsePresets(JsonElement root)
    {
        var names = new List<string>();
        var list = root;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("presets", out var presets))
        {
            list = presets;
        }

        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var name = item.ValueKind == JsonValueKind.String ? item.GetString() : GetString(item, "name") ?? GetString(item, "id");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }
        else if (list.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in list.EnumerateObject())
            {
                names.Add(property.Name);
            }
        }

        return names;
    }

    /// <summary>
    /// Parses POST /browse for a "ytsearchN:" query: { status, title, entries: [{ url, title, duration }] }.
    /// Used only as the fallback search, so it carries no channel, date or view count.
    /// </summary>
    public static SearchPage ParseBrowse(JsonElement root)
    {
        var items = new List<VideoResult>();
        if (!string.Equals(GetString(root, "status"), "ok", StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            return new SearchPage(items, null);
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (!VideoIdParser.TryExtract(GetString(entry, "url"), out var videoId))
            {
                continue;
            }

            int? duration = null;
            if (entry.TryGetProperty("duration", out var durationElement) && durationElement.ValueKind == JsonValueKind.Number
                && durationElement.TryGetDouble(out var seconds) && seconds > 0)
            {
                duration = (int)Math.Round(seconds);
            }

            items.Add(new VideoResult
            {
                VideoId = videoId,
                Title = GetString(entry, "title") ?? videoId,
                DurationSeconds = duration,
                DurationText = duration is null ? null : InnerTubeParser.FormatDuration(duration.Value),
                IsLive = duration is null,
                ThumbnailUrl = InnerTubeParser.ThumbnailUrl(videoId)
            });
        }

        return new SearchPage(items, null);
    }

    private static void ReadEntries(JsonElement root, string property, List<MeTubeEntry> target)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(property, out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in list.EnumerateArray())
        {
            // Older MeTube versions sent [url, entry] pairs; current ones send entries.
            var entry = item;
            if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() == 2)
            {
                entry = item[1];
            }

            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            target.Add(new MeTubeEntry
            {
                Id = GetString(entry, "id"),
                Url = GetString(entry, "url"),
                Title = GetString(entry, "title"),
                Status = GetString(entry, "status"),
                Percent = GetDouble(entry, "percent"),
                Filename = GetString(entry, "filename"),
                Folder = GetString(entry, "folder"),
                Timestamp = GetLong(entry, "timestamp"),
                Msg = GetString(entry, "msg"),
                Error = GetString(entry, "error")
            });
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private static string? GetString(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? GetDouble(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : null;

    private static long? GetLong(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (value.TryGetInt64(out var integer))
        {
            return integer;
        }

        return value.TryGetDouble(out var number) && number < long.MaxValue ? (long)number : null;
    }
}
