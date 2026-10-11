using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MeTube.Search;

/// <summary>
/// Turns whatever a user pastes (a bare id or any common YouTube link) into a
/// validated 11-character video id. Every download URL is rebuilt from that id,
/// so nothing user-supplied ever reaches MeTube verbatim. That also drops
/// "t=" start offsets, which MeTube would otherwise turn into a clip.
/// </summary>
public static partial class VideoIdParser
{
    /// <summary>Returns true when the value is a syntactically valid video id.</summary>
    public static bool IsValidId([NotNullWhen(true)] string? id) => id is not null && IdRegex().IsMatch(id);

    /// <summary>Builds the canonical watch URL for a validated id.</summary>
    public static string WatchUrl(string videoId) => "https://www.youtube.com/watch?v=" + videoId;

    /// <summary>Tries to extract a video id from a bare id or a YouTube URL.</summary>
    public static bool TryExtract(string? input, [NotNullWhen(true)] out string? videoId)
    {
        videoId = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim();
        if (IsValidId(text))
        {
            videoId = text;
            return true;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        foreach (var prefix in new[] { "www.", "m.", "music." })
        {
            if (host.StartsWith(prefix, StringComparison.Ordinal))
            {
                host = host[prefix.Length..];
                break;
            }
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? candidate = null;
        if (host == "youtu.be")
        {
            candidate = segments.Length > 0 ? segments[0] : null;
        }
        else if (host is "youtube.com" or "youtube-nocookie.com")
        {
            if (segments.Length == 0 || string.Equals(segments[0], "watch", StringComparison.OrdinalIgnoreCase))
            {
                candidate = GetQueryValue(uri, "v");
            }
            else if (segments.Length >= 2 && IsIdPathPrefix(segments[0]))
            {
                candidate = segments[1];
            }
        }

        if (IsValidId(candidate))
        {
            videoId = candidate;
            return true;
        }

        return false;
    }

    private static bool IsIdPathPrefix(string segment) =>
        segment.Equals("shorts", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("live", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("embed", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("v", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("e", StringComparison.OrdinalIgnoreCase);

    private static string? GetQueryValue(Uri uri, string key)
    {
        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var name = separator < 0 ? pair : pair[..separator];
            if (string.Equals(Uri.UnescapeDataString(name), key, StringComparison.Ordinal))
            {
                return separator < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        return null;
    }

    // \z, not $: in .NET "$" also matches before a trailing newline.
    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}\z")]
    private static partial Regex IdRegex();
}
