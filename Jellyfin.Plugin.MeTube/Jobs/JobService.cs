using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MeTube.Library;
using Jellyfin.Plugin.MeTube.MeTube;
using Jellyfin.Plugin.MeTube.Models;
using Jellyfin.Plugin.MeTube.Search;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MeTube.Jobs;

/// <summary>Result of an Add request, with the HTTP status the API should return.</summary>
public sealed record AddOutcome(int StatusCode, JobRecord? Job, Guid? LibraryItemId, bool AlreadyInLibrary, string? Error);

/// <summary>Handles "Add": checks, limits, and the MeTube call.</summary>
public sealed class JobService
{
    /// <summary>Adds per minute per non-admin user.</summary>
    public const int AddsPerMinute = 10;

    private static readonly TimeSpan OrphanRefreshInterval = TimeSpan.FromMinutes(2);

    private readonly JobStore _store;
    private readonly MeTubeClient _meTube;
    private readonly VideoInfoService _videoInfo;
    private readonly YouTubeGuard _guard;
    private readonly LibraryIndex _libraryIndex;
    private readonly LibraryLocator _locator;
    private readonly LibraryRefresher _refresher;
    private readonly DownloadWatcher _watcher;
    private readonly ILogger<JobService> _logger;

    /// <summary>Initializes a new instance of the <see cref="JobService"/> class.</summary>
    public JobService(
        JobStore store,
        MeTubeClient meTube,
        VideoInfoService videoInfo,
        YouTubeGuard guard,
        LibraryIndex libraryIndex,
        LibraryLocator locator,
        LibraryRefresher refresher,
        DownloadWatcher watcher,
        ILogger<JobService> logger)
    {
        _store = store;
        _meTube = meTube;
        _videoInfo = videoInfo;
        _guard = guard;
        _libraryIndex = libraryIndex;
        _locator = locator;
        _refresher = refresher;
        _watcher = watcher;
        _logger = logger;
    }

    /// <summary>Builds the MeTube add options from the current configuration.</summary>
    public static MeTubeAddOptions CurrentOptions()
    {
        var config = Plugin.Config;
        return new MeTubeAddOptions(
            (config.Folder ?? string.Empty).Trim().Trim('/', '\\'),
            (config.Presets ?? Array.Empty<string>()).Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
            string.IsNullOrWhiteSpace(config.Quality) ? "best" : config.Quality.Trim(),
            string.IsNullOrWhiteSpace(config.Format) ? "any" : config.Format.Trim(),
            string.IsNullOrWhiteSpace(config.Codec) ? "auto" : config.Codec.Trim());
    }

    /// <summary>Asks Jellyfin to scan when a file is in the folder but has no item yet (at most every 2 minutes).</summary>
    public void RefreshIfOrphan(Guid? itemId)
    {
        if (itemId is null && _locator.Resolve() is { } library)
        {
            _refresher.QueueRefreshThrottled(library.Id, OrphanRefreshInterval);
        }
    }

    /// <summary>Adds a video for a user.</summary>
    public async Task<AddOutcome> AddAsync(Guid userId, string? userName, bool isAdmin, string videoId, CancellationToken cancellationToken)
    {
        var config = Plugin.Config;
        if (string.IsNullOrWhiteSpace(config.MeTubeUrl))
        {
            return new AddOutcome(503, null, null, false, "MeTube is not set up yet. Ask an administrator to configure the plugin.");
        }

        // These two checks make no YouTube call, so they come before the limits.
        if (_libraryIndex.TryFindFile(videoId, out var existingFile))
        {
            var existingItem = _libraryIndex.ResolveItemId(videoId, existingFile);
            RefreshIfOrphan(existingItem);
            return new AddOutcome(200, null, existingItem, true, null);
        }

        var now = DateTimeOffset.UtcNow;
        var open = await _store.AttachToOpenAsync(videoId, userId, now, cancellationToken).ConfigureAwait(false);
        if (open is not null)
        {
            return new AddOutcome(200, open, null, false, null);
        }

        // Every add costs YouTube requests (a lookup here, yt-dlp's probe in MeTube),
        // so it sits behind the same guard as search.
        if (!isAdmin)
        {
            if (_guard.IsPaused(now))
            {
                return new AddOutcome(503, null, null, false, "YouTube is limiting requests from this server. Try again in a few minutes.");
            }

            if (!_guard.TryCount(userId, "add", AddsPerMinute, now))
            {
                return new AddOutcome(429, null, null, false, "Too many adds. Wait a minute and try again.");
            }
        }

        var info = await _videoInfo.GetForAddAsync(videoId, cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            if (!isAdmin)
            {
                return new AddOutcome(502, null, null, false, "Could not look this video up on YouTube. Try again later.");
            }
        }
        else
        {
            if (!info.Available)
            {
                return new AddOutcome(404, null, null, false, info.UnavailableReason ?? "Video unavailable.");
            }

            if (info.IsLive)
            {
                return new AddOutcome(422, null, null, false, "Live streams can't be added.");
            }

            if (info.IsUpcoming)
            {
                return new AddOutcome(422, null, null, false, "This video hasn't been published yet.");
            }

            if (!isAdmin && config.MaxDurationMinutes > 0)
            {
                if (info.DurationSeconds is null)
                {
                    return new AddOutcome(422, null, null, false, "This video's length isn't known yet (it may still be processing), so it can't be added.");
                }

                if (info.DurationSeconds > config.MaxDurationMinutes * 60L)
                {
                    return new AddOutcome(422, null, null, false, string.Format(CultureInfo.InvariantCulture, "Videos longer than {0} minutes can't be added.", config.MaxDurationMinutes));
                }
            }
        }

        var options = CurrentOptions();
        now = DateTimeOffset.UtcNow;
        var candidate = new JobRecord
        {
            VideoId = videoId,
            Title = info?.Title ?? videoId,
            Channel = info?.Channel,
            DurationSeconds = info?.DurationSeconds,
            UserId = userId,
            UserName = userName,
            Folder = options.Folder,
            State = JobState.Queued,
            MatchSinceNs = JobTracker.ToUnixNanoseconds(now - JobTracker.MatchSlack)
        };

        var (outcome, job) = await _store.ReserveAsync(candidate, config.DailyLimitPerUser, isAdmin, now, cancellationToken).ConfigureAwait(false);
        if (outcome == ReserveOutcome.LimitReached)
        {
            return new AddOutcome(429, null, null, false, string.Format(CultureInfo.InvariantCulture, "You can add {0} videos per 24 hours.", config.DailyLimitPerUser));
        }

        if (outcome == ReserveOutcome.Existing || job is null)
        {
            return new AddOutcome(200, job, null, false, null);
        }

        // Deliberately not tied to the request: if the browser goes away while MeTube
        // is still probing the video, the add should still complete and be tracked.
        // The watcher leaves the job alone until MeTube has answered.
        _watcher.BeginSubmit(job.Id);
        try
        {
            MeTubeAddResult result;
            try
            {
                result = await _meTube.AddAsync(config.MeTubeUrl, videoId, options, CancellationToken.None).ConfigureAwait(false);
            }
            catch (MeTubeException ex) when (ex.IsTimeout)
            {
                // MeTube keeps working on a request after the client gives up, so the
                // video is most likely being queued right now. Keep tracking it; the
                // watcher marks it Missing if it never shows up.
                _logger.LogWarning("MeTube: add of {VideoId} timed out; tracking it anyway.", videoId);
                return new AddOutcome(200, job, null, false, null);
            }
            catch (MeTubeException ex)
            {
                _logger.LogWarning(ex, "MeTube: add of {VideoId} failed.", videoId);
                await _store.RemoveAsync(job.Id, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                return new AddOutcome(502, null, null, false, isAdmin ? ex.Message : "Could not reach the download server. Try again later.");
            }

            if (result.Outcome == MeTubeAddOutcome.Error)
            {
                _logger.LogWarning("MeTube refused {VideoId}: {Message}", videoId, result.Message);
                if (result.ConfigProblem)
                {
                    // A setup problem (unknown preset, bad option) is not the user's
                    // doing: don't count it, and don't show internals to non-admins.
                    await _store.RemoveAsync(job.Id, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    return new AddOutcome(502, null, null, false, isAdmin
                        ? "MeTube: " + result.Message
                        : "The download server rejected the request. Ask an administrator to check the MeTube settings.");
                }

                // yt-dlp refused this video (members-only, age-gated, removed...). Keep
                // the attempt as Failed: it counts against the daily limit, so the same
                // refusal cannot be retried endlessly at YouTube's expense.
                var message = "MeTube: " + (result.Message ?? "could not add the video.");
                await _store.UpdateAsync(
                    job.Id,
                    j =>
                    {
                        j.State = JobState.Failed;
                        j.Error = message;
                    },
                    DateTimeOffset.UtcNow,
                    CancellationToken.None).ConfigureAwait(false);
                return new AddOutcome(502, null, null, false, message);
            }

            if (result.Outcome == MeTubeAddOutcome.AlreadyQueued)
            {
                // The queue entry predates this job, so the timestamp guard would never
                // let it match. Active entries are preferred over finished ones, so
                // accepting any timestamp is safe while it is queued, and if it vanishes
                // the newest finished entry for the URL is this same video anyway.
                job = await _store.UpdateAsync(job.Id, j => j.MatchSinceNs = 0, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false) ?? job;
            }

            _logger.LogInformation("MeTube: {User} added {VideoId} ({Title}).", userName ?? userId.ToString(), videoId, job.Title);
            return new AddOutcome(200, job, null, false, null);
        }
        finally
        {
            _watcher.EndSubmit(job.Id);
            _watcher.Nudge();
        }
    }
}
