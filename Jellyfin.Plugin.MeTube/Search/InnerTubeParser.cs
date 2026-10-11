using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MeTube.Models;

namespace Jellyfin.Plugin.MeTube.Search;

/// <summary>
/// Parses responses from YouTube's internal web API ("InnerTube"). Pure functions
/// over a JSON document so they can be tested against captured responses.
/// </summary>
public static partial class InnerTubeParser
{
    /// <summary>Builds the 16:9 thumbnail URL for a video id.</summary>
    public static string ThumbnailUrl(string videoId) => "https://i.ytimg.com/vi/" + videoId + "/mqdefault.jpg";

    /// <summary>
    /// Parses a /youtubei/v1/search response, either the first page or a
    /// continuation page. The continuation token is read only from the result
    /// list itself: the response header carries filter chips with their own
    /// continuation commands, and picking one of those would re-run a filtered
    /// search instead of loading the next page.
    /// </summary>
    public static SearchPage ParseSearch(JsonElement root)
    {
        var items = new List<VideoResult>();
        string? continuation = null;

        if (TryGetPath(root, out var sections, "contents", "twoColumnSearchResultsRenderer", "primaryContents", "sectionListRenderer", "contents"))
        {
            ReadSections(sections, items, ref continuation);
        }

        if (root.TryGetProperty("onResponseReceivedCommands", out var commands) && commands.ValueKind == JsonValueKind.Array)
        {
            foreach (var command in commands.EnumerateArray())
            {
                if (TryGetPath(command, out var continuationItems, "appendContinuationItemsAction", "continuationItems"))
                {
                    ReadSections(continuationItems, items, ref continuation);
                }
            }
        }

        return new SearchPage(items, continuation);
    }

    /// <summary>Parses one videoRenderer object; returns null when it has no valid id.</summary>
    public static VideoResult? ParseVideoRenderer(JsonElement renderer)
    {
        var videoId = GetString(renderer, "videoId");
        if (!VideoIdParser.IsValidId(videoId))
        {
            return null;
        }

        var durationText = Text(renderer, "lengthText");
        var isUpcoming = renderer.TryGetProperty("upcomingEventData", out _);

        // Live streams carry no length and no time-status overlay, and YouTube
        // dropped the explicit LIVE badge from search results. A missing length
        // is therefore the signal; Add re-checks with an authoritative player
        // lookup before anything is queued.
        var isLive = !isUpcoming && (durationText is null || HasLiveMarker(renderer));

        string? channelId = null;
        if (TryGetPath(renderer, out var runs, "ownerText", "runs")
            && runs.ValueKind == JsonValueKind.Array
            && runs.GetArrayLength() > 0
            && TryGetPath(runs[0], out var browseId, "navigationEndpoint", "browseEndpoint", "browseId")
            && browseId.ValueKind == JsonValueKind.String)
        {
            channelId = browseId.GetString();
        }

        var publishedText = Text(renderer, "publishedTimeText");
        return new VideoResult
        {
            VideoId = videoId,
            Title = Text(renderer, "title") ?? videoId,
            Channel = Text(renderer, "ownerText") ?? Text(renderer, "longBylineText") ?? Text(renderer, "shortBylineText"),
            ChannelId = channelId,
            DurationText = isLive ? null : durationText,
            DurationSeconds = isLive ? null : ParseDuration(durationText),
            PublishedText = publishedText,
            ViewsText = Text(renderer, "shortViewCountText") ?? Text(renderer, "viewCountText"),
            ViewCount = ParseViewCount(Text(renderer, "viewCountText")),
            AgeSeconds = ParseAge(publishedText),
            IsLive = isLive,
            IsUpcoming = isUpcoming,
            ThumbnailUrl = ThumbnailUrl(videoId)
        };
    }

    /// <summary>
    /// Reads the exact count from the long form ("442,807,905 views", "1.101.821
    /// weergaven"): all digits, whatever the thousands separator. Abbreviated forms
    /// ("442M views") are refused rather than guessed.
    /// </summary>
    public static long? ParseViewCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (text.Contains("No views", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (AbbreviatedRegex().IsMatch(text))
        {
            return null;
        }

        var digits = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
            }
        }

        return digits.Length is > 0 and <= 18 && long.TryParse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : null;
    }

    /// <summary>
    /// Parses YouTube's relative dates in English ("3 days ago", "Streamed 1 year ago")
    /// or Dutch ("16 jaar geleden", "3 dagen geleden") into an approximate age in
    /// seconds. Other languages sort last.
    /// </summary>
    public static long? ParseAge(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = AgeRegex().Match(text);
        if (!match.Success)
        {
            match = DutchAgeRegex().Match(text);
            if (!match.Success)
            {
                return null;
            }
        }

        if (!long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
        {
            return null;
        }

        var name = match.Groups[2].Value.ToLowerInvariant();
        long unit = name switch
        {
            _ when name.StartsWith("sec", StringComparison.Ordinal) => 1,
            _ when name.StartsWith("min", StringComparison.Ordinal) => 60,
            "hour" or "uur" => 3600,
            _ when name.StartsWith("da", StringComparison.Ordinal) => 86400,
            _ when name.StartsWith("we", StringComparison.Ordinal) => 7 * 86400,
            _ when name.StartsWith("m", StringComparison.Ordinal) => 30 * 86400,
            _ => 365 * 86400
        };
        return amount * unit;
    }

    /// <summary>
    /// Parses a /youtubei/v1/player response. The web client without a proof-of-origin
    /// token usually gets "UNPLAYABLE", but videoDetails (length, live flags, title)
    /// is still filled in, and that is all this needs. A response without
    /// videoDetails means the video does not exist or was removed.
    /// </summary>
    public static VideoInfo ParsePlayer(JsonElement root, string requestedId)
    {
        if (!root.TryGetProperty("videoDetails", out var details) || details.ValueKind != JsonValueKind.Object)
        {
            string? reason = null;
            if (root.TryGetProperty("playabilityStatus", out var status))
            {
                reason = GetString(status, "reason") ?? GetString(status, "status");
            }

            return new VideoInfo
            {
                VideoId = requestedId,
                Available = false,
                UnavailableReason = string.IsNullOrWhiteSpace(reason) ? "Video unavailable" : reason,
                FromPlayer = true
            };
        }

        var videoId = GetString(details, "videoId");
        if (!string.Equals(videoId, requestedId, StringComparison.Ordinal))
        {
            return new VideoInfo { VideoId = requestedId, Available = false, UnavailableReason = "Video unavailable", FromPlayer = true };
        }

        if (GetBool(details, "isPrivate"))
        {
            return new VideoInfo { VideoId = requestedId, Available = false, UnavailableReason = "This video is private", FromPlayer = true };
        }

        // A stream that just ended is still "post-live DVR" while YouTube processes it:
        // no length yet, and yt-dlp would fetch the whole recording from the start.
        var isLive = GetBool(details, "isLive") || GetBool(details, "isPostLiveDvr");
        var isUpcoming = GetBool(details, "isUpcoming");
        int? duration = null;
        var lengthText = GetString(details, "lengthSeconds");
        if (int.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            duration = seconds;
        }

        return new VideoInfo
        {
            VideoId = requestedId,
            Title = GetString(details, "title") ?? requestedId,
            Channel = GetString(details, "author"),
            DurationSeconds = isLive || isUpcoming ? null : duration,
            IsLive = isLive,
            IsUpcoming = isUpcoming,
            Available = true,
            FromPlayer = true
        };
    }

    /// <summary>Parses "1:02:03", "12:34" or "0:19" into seconds.</summary>
    public static int? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var total = 0;
        foreach (var part in text.Trim().Split(':'))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            total = checked((total * 60) + value);
        }

        return total;
    }

    /// <summary>Formats seconds like YouTube does: 0:19, 12:34, 1:02:03.</summary>
    public static string FormatDuration(int seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes}:{span.Seconds:00}");
    }

    private static void ReadSections(JsonElement sections, List<VideoResult> items, ref string? continuation)
    {
        if (sections.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var section in sections.EnumerateArray())
        {
            if (TryGetPath(section, out var contents, "itemSectionRenderer", "contents") && contents.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in contents.EnumerateArray())
                {
                    if (entry.TryGetProperty("videoRenderer", out var renderer))
                    {
                        var video = ParseVideoRenderer(renderer);
                        if (video is not null)
                        {
                            items.Add(video);
                        }
                    }
                }
            }
            else if (TryGetPath(section, out var token, "continuationItemRenderer", "continuationEndpoint", "continuationCommand", "token")
                && token.ValueKind == JsonValueKind.String)
            {
                continuation = token.GetString();
            }
        }
    }

    private static bool HasLiveMarker(JsonElement renderer)
    {
        if (renderer.TryGetProperty("badges", out var badges) && badges.ValueKind == JsonValueKind.Array)
        {
            foreach (var badge in badges.EnumerateArray())
            {
                if (badge.TryGetProperty("metadataBadgeRenderer", out var meta)
                    && (string.Equals(GetString(meta, "style"), "BADGE_STYLE_TYPE_LIVE_NOW", StringComparison.Ordinal)
                        || string.Equals(GetString(meta, "label"), "LIVE", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        if (renderer.TryGetProperty("thumbnailOverlays", out var overlays) && overlays.ValueKind == JsonValueKind.Array)
        {
            foreach (var overlay in overlays.EnumerateArray())
            {
                if (overlay.TryGetProperty("thumbnailOverlayTimeStatusRenderer", out var status)
                    && string.Equals(GetString(status, "style"), "LIVE", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Reads a YouTube text object: either { simpleText } or { runs: [{ text }] }.</summary>
    private static string? Text(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var simple = GetString(value, "simpleText");
        if (!string.IsNullOrWhiteSpace(simple))
        {
            return simple.Trim();
        }

        if (value.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var run in runs.EnumerateArray())
            {
                builder.Append(GetString(run, "text"));
            }

            var joined = builder.ToString().Trim();
            return joined.Length == 0 ? null : joined;
        }

        return null;
    }

    private static string? GetString(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static bool TryGetPath(JsonElement element, out JsonElement result, params string[] path)
    {
        result = element;
        foreach (var name in path)
        {
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(name, out result))
            {
                result = default;
                return false;
            }
        }

        return true;
    }

    // "3 days ago", "Streamed 1 year ago", "Premiered 2 weeks ago".
    [GeneratedRegex(@"(\d+)\s+(second|minute|hour|day|week|month|year)s?\s+ago", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AgeRegex();

    // "3 dagen geleden", "1 uur geleden", "Gestreamd 16 jaar geleden".
    [GeneratedRegex(@"(\d+)\s+(seconden?|minuut|minuten|uur|dag(?:en)?|we(?:ek|ken)|maand(?:en)?|jaar)\s+geleden", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DutchAgeRegex();

    // "442M views", "1.2K views", "1,1 mln. weergaven": rounded, so not usable as a count.
    [GeneratedRegex(@"\d\s*(?:[KMB]\b|mln|mld|bn\b|dzd)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AbbreviatedRegex();
}
