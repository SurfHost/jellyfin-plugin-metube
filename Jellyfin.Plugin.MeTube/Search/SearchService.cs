using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MeTube.MeTube;
using Jellyfin.Plugin.MeTube.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MeTube.Search;

/// <summary>Result of a search request, with the HTTP status the API should return.</summary>
public sealed record SearchOutcome(int StatusCode, SearchPage? Page, string? Source, string? Error);

/// <summary>
/// Runs YouTube searches for the API, behind the shared <see cref="YouTubeGuard"/>: a
/// per-user rate limit, at most two searches at a time, a 5-minute result cache, and
/// the common pause that any 429/403 from YouTube starts. MeTube's yt-dlp search is
/// used only when YouTube answered but nothing could be parsed: both hit the same
/// YouTube endpoint, so falling back on errors would only double the load when it
/// hurts most.
/// </summary>
public sealed partial class SearchService
{
    /// <summary>Longest accepted query.</summary>
    public const int MaxQueryLength = 200;

    private const int PerUserPerMinute = 20;
    private const int CacheEntries = 300;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly InnerTubeClient _innerTube;
    private readonly MeTubeClient _meTube;
    private readonly VideoInfoService _videoInfo;
    private readonly YouTubeGuard _guard;
    private readonly ILogger<SearchService> _logger;
    private readonly SemaphoreSlim _concurrency = new(2, 2);
    private readonly ConcurrentDictionary<string, (SearchPage Page, string Source, DateTimeOffset At)> _cache = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="SearchService"/> class.</summary>
    public SearchService(InnerTubeClient innerTube, MeTubeClient meTube, VideoInfoService videoInfo, YouTubeGuard guard, ILogger<SearchService> logger)
    {
        _innerTube = innerTube;
        _meTube = meTube;
        _videoInfo = videoInfo;
        _guard = guard;
        _logger = logger;
    }

    /// <summary>Turns control characters (tabs, newlines) into spaces and collapses whitespace; null when nothing is left.</summary>
    public static string? NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var cleaned = new string(query.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        cleaned = WhitespaceRegex().Replace(cleaned, " ").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>Returns true when the continuation token looks like one YouTube issued.</summary>
    public static bool IsValidContinuation(string? token) =>
        !string.IsNullOrEmpty(token) && token.Length <= 4096 && ContinuationRegex().IsMatch(token);

    /// <summary>Runs a search for a user.</summary>
    public async Task<SearchOutcome> SearchAsync(Guid userId, string? rawQuery, string? continuation, CancellationToken cancellationToken)
    {
        var query = NormalizeQuery(rawQuery);
        if (!string.IsNullOrEmpty(continuation))
        {
            if (!IsValidContinuation(continuation))
            {
                return new SearchOutcome(400, null, null, "Invalid page token.");
            }
        }
        else if (query is null)
        {
            return new SearchOutcome(400, null, null, "Type something to search for.");
        }
        else if (query.Length > MaxQueryLength)
        {
            return new SearchOutcome(400, null, null, "That search is too long.");
        }

        var now = DateTimeOffset.UtcNow;
        var settings = VideoInfoService.CurrentSettings();
        var key = string.Join('|', settings.Region, settings.Language, continuation ?? string.Empty, (query ?? string.Empty).ToLowerInvariant());
        if (_cache.TryGetValue(key, out var hit) && now - hit.At < CacheFor)
        {
            return new SearchOutcome(200, hit.Page, hit.Source, null);
        }

        if (_guard.IsPaused(now))
        {
            return new SearchOutcome(503, null, null, "YouTube is limiting requests from this server. Try again in a few minutes.");
        }

        if (!_guard.TryCount(userId, "search", PerUserPerMinute, now))
        {
            return new SearchOutcome(429, null, null, "Too many searches. Wait a minute and try again.");
        }

        if (!await _concurrency.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false))
        {
            return new SearchOutcome(503, null, null, "Search is busy. Try again in a moment.");
        }

        try
        {
            SearchPage page;
            var source = "youtube";
            var parseFailed = false;
            try
            {
                page = await _innerTube.SearchAsync(query, continuation, settings, cancellationToken).ConfigureAwait(false);
                parseFailed = page.Items.Count == 0 && string.IsNullOrEmpty(continuation);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "MeTube: could not read YouTube's search answer.");
                page = new SearchPage(Array.Empty<VideoResult>(), null);
                parseFailed = string.IsNullOrEmpty(continuation);
            }

            if (parseFailed && query is not null && !string.IsNullOrWhiteSpace(Plugin.Config.MeTubeUrl))
            {
                try
                {
                    var fallback = await _meTube.BrowseSearchAsync(Plugin.Config.MeTubeUrl, query, 20, cancellationToken).ConfigureAwait(false);
                    if (fallback.Items.Count > 0)
                    {
                        _logger.LogInformation("MeTube: YouTube search returned nothing usable; used MeTube's yt-dlp search instead.");
                        page = fallback;
                        source = "metube";
                    }
                }
                catch (MeTubeException ex)
                {
                    _logger.LogWarning(ex, "MeTube: fallback search through MeTube failed.");
                }
            }

            foreach (var item in page.Items)
            {
                _videoInfo.Remember(item);
            }

            // An empty page that neither YouTube nor the fallback could fill may be a
            // hiccup; don't serve it to everyone as "no videos found" for 5 minutes.
            if (page.Items.Count > 0 || !parseFailed)
            {
                StoreInCache(key, page, source, now);
            }

            return new SearchOutcome(200, page, source, null);
        }
        catch (YouTubeHttpException ex) when (ex.IsThrottled)
        {
            _guard.Pause(DateTimeOffset.UtcNow);
            _logger.LogWarning("MeTube: YouTube answered HTTP {Status} to a search; pausing YouTube calls for {Minutes} minutes.", ex.StatusCode, YouTubeGuard.PauseFor.TotalMinutes);
            return new SearchOutcome(503, null, null, "YouTube is limiting requests from this server. Try again in a few minutes.");
        }
        catch (YouTubeHttpException ex)
        {
            _logger.LogWarning("MeTube: YouTube search failed with HTTP {Status}.", ex.StatusCode);
            return new SearchOutcome(502, null, null, "YouTube search failed. Try again later.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "MeTube: could not reach YouTube.");
            return new SearchOutcome(502, null, null, "Could not reach YouTube. Is the server online?");
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private void StoreInCache(string key, SearchPage page, string source, DateTimeOffset now)
    {
        if (_cache.Count >= CacheEntries)
        {
            foreach (var stale in _cache.Where(e => now - e.Value.At >= CacheFor).Select(e => e.Key).ToList())
            {
                _cache.TryRemove(stale, out _);
            }

            if (_cache.Count >= CacheEntries)
            {
                _cache.Clear();
            }
        }

        _cache[key] = (page, source, now);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // \z, not $: in .NET "$" also matches before a trailing newline.
    [GeneratedRegex(@"^[A-Za-z0-9_%=-]+\z")]
    private static partial Regex ContinuationRegex();
}
