using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MeTube.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MeTube.Search;

/// <summary>
/// Remembers what search results said about each video (title, length, live) for an
/// hour, and answers the authoritative question Add needs: may this be queued?
/// Search results cannot tell a live stream apart from a video whose length is
/// missing, and a pasted link has no search result at all, so Add always asks
/// YouTube's player endpoint first and only falls back to the remembered result.
/// </summary>
public sealed class VideoInfoService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);
    private const int MaxEntries = 2000;

    private readonly InnerTubeClient _innerTube;
    private readonly YouTubeGuard _guard;
    private readonly ILogger<VideoInfoService> _logger;
    private readonly ConcurrentDictionary<string, (VideoInfo Info, DateTimeOffset At)> _cache = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="VideoInfoService"/> class.</summary>
    public VideoInfoService(InnerTubeClient innerTube, YouTubeGuard guard, ILogger<VideoInfoService> logger)
    {
        _innerTube = innerTube;
        _guard = guard;
        _logger = logger;
    }

    /// <summary>Builds the InnerTube settings from the current configuration.</summary>
    public static InnerTubeSettings CurrentSettings()
    {
        var config = Plugin.Config;
        return new InnerTubeSettings(
            string.IsNullOrWhiteSpace(config.InnerTubeClientVersion) ? "2.20261009.01.00" : config.InnerTubeClientVersion.Trim(),
            string.IsNullOrWhiteSpace(config.SearchLanguage) ? "en" : config.SearchLanguage.Trim(),
            (config.SearchRegion ?? string.Empty).Trim());
    }

    /// <summary>Remembers what a search result said about a video.</summary>
    public void Remember(VideoResult result)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(result.VideoId, out var existing) && existing.Info.FromPlayer && now - existing.At < CacheFor)
        {
            return;
        }

        Store(new VideoInfo
        {
            VideoId = result.VideoId,
            Title = result.Title,
            Channel = result.Channel,
            DurationSeconds = result.DurationSeconds,
            IsLive = result.IsLive,
            IsUpcoming = result.IsUpcoming
        });
    }

    /// <summary>
    /// Returns the best available info: a player lookup (cached for an hour), or
    /// what search said when YouTube cannot be reached. Null when neither exists.
    /// </summary>
    public async Task<VideoInfo?> GetForAddAsync(string videoId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        _cache.TryGetValue(videoId, out var cached);
        var fresh = cached.Info is not null && now - cached.At < CacheFor;

        // Only a positive player answer is reused. "Unavailable", live and upcoming
        // can change at any moment (a bot check passing, a premiere going public), so
        // those are asked again instead of blocking Add for an hour.
        if (fresh && cached.Info!.FromPlayer && cached.Info.Available && !cached.Info.IsLive && !cached.Info.IsUpcoming)
        {
            return cached.Info;
        }

        if (_guard.IsPaused(now))
        {
            return fresh ? cached.Info : null;
        }

        try
        {
            var info = await _innerTube.GetVideoInfoAsync(videoId, CurrentSettings(), cancellationToken).ConfigureAwait(false);
            if (fresh && string.IsNullOrEmpty(info.Channel))
            {
                info.Channel = cached.Info!.Channel;
            }

            Store(info);
            return info;
        }
        catch (YouTubeHttpException ex) when (ex.IsThrottled)
        {
            _guard.Pause(DateTimeOffset.UtcNow);
            _logger.LogWarning("MeTube: YouTube answered HTTP {Status} to a video lookup; pausing YouTube calls for {Minutes} minutes.", ex.StatusCode, YouTubeGuard.PauseFor.TotalMinutes);
            return fresh ? cached.Info : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "MeTube: YouTube lookup for {VideoId} failed; using the search result instead.", videoId);
            return fresh ? cached.Info : null;
        }
    }

    private void Store(VideoInfo info)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cache.Count >= MaxEntries)
        {
            foreach (var stale in _cache.Where(e => now - e.Value.At >= CacheFor).Select(e => e.Key).ToList())
            {
                _cache.TryRemove(stale, out _);
            }

            if (_cache.Count >= MaxEntries)
            {
                _cache.Clear();
            }
        }

        _cache[info.VideoId] = (info, now);
    }
}
