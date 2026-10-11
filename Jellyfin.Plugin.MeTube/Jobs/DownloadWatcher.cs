using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MeTube.Library;
using Jellyfin.Plugin.MeTube.MeTube;
using Jellyfin.Plugin.MeTube.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MeTube.Jobs;

/// <summary>
/// Follows open jobs until their video is in the library. Polls MeTube's /history
/// every 10 seconds, but only while a job is queued or downloading; with nothing
/// open it does no network traffic at all. When a download finishes it queues a
/// scan of just the YouTube library, then waits for the item to appear. Once a
/// minute it also looks again at recently closed jobs whose file may have
/// arrived late. Every ten minutes it tidies the library folder (see
/// <see cref="MaintainAsync"/>).
/// </summary>
public sealed class DownloadWatcher : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ClosedCheckInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(10);

    private readonly JobStore _store;
    private readonly MeTubeClient _meTube;
    private readonly LibraryLocator _locator;
    private readonly LibraryIndex _libraryIndex;
    private readonly LibraryRefresher _refresher;
    private readonly LibraryMaintenance _maintenance;
    private readonly FolderMerger _merger;
    private readonly ILogger<DownloadWatcher> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly ConcurrentDictionary<Guid, byte> _submitting = new();

    // Only touched from the single watcher loop.
    private readonly Dictionary<Guid, (int Count, DateTimeOffset First)> _misses = new();
    private readonly HashSet<Guid> _rescanned = new();
    private DateTimeOffset _lastClosedCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _lastMaintenance = DateTimeOffset.MinValue;

    /// <summary>Initializes a new instance of the <see cref="DownloadWatcher"/> class.</summary>
    public DownloadWatcher(
        JobStore store,
        MeTubeClient meTube,
        LibraryLocator locator,
        LibraryIndex libraryIndex,
        LibraryRefresher refresher,
        LibraryMaintenance maintenance,
        FolderMerger merger,
        ILogger<DownloadWatcher> logger)
    {
        _store = store;
        _meTube = meTube;
        _locator = locator;
        _libraryIndex = libraryIndex;
        _refresher = refresher;
        _maintenance = maintenance;
        _merger = merger;
        _logger = logger;
    }

    /// <summary>Wakes the watcher right away, after a job was added.</summary>
    public void Nudge()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Already awake.
        }
    }

    /// <summary>
    /// Marks a job as still being submitted. MeTube answers /add only after yt-dlp has
    /// probed the video, which can take many seconds, and the entry does not exist in
    /// /history until then; a job must not be judged while that is going on.
    /// </summary>
    public void BeginSubmit(Guid jobId) => _submitting[jobId] = 0;

    /// <summary>Ends <see cref="BeginSubmit"/>.</summary>
    public void EndSubmit(Guid jobId) => _submitting.TryRemove(jobId, out _);

    /// <summary>One pass over all jobs. Internal so tests and diagnostics can call it.</summary>
    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var location = _locator.Resolve();
        await ForgetRemovedAsync(_libraryIndex.DrainRemovedVideoIds(), location, now, cancellationToken).ConfigureAwait(false);

        var all = await _store.ListAsync(cancellationToken).ConfigureAwait(false);

        if (now - _lastClosedCheck >= ClosedCheckInterval)
        {
            _lastClosedCheck = now;
            await RecheckClosedAsync(all, location, now, cancellationToken).ConfigureAwait(false);
        }

        var jobs = all.Where(j => j.IsOpen).ToList();
        if (jobs.Count == 0)
        {
            _misses.Clear();
            _rescanned.Clear();
            return;
        }

        var config = Plugin.Config;
        var refresh = false;

        MeTubeHistory? history = null;
        if (jobs.Any(j => j.State is JobState.Queued or JobState.Downloading) && !string.IsNullOrWhiteSpace(config.MeTubeUrl))
        {
            try
            {
                history = await _meTube.GetHistoryAsync(config.MeTubeUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (MeTubeException ex)
            {
                _logger.LogDebug(ex, "MeTube: history poll failed; will retry.");
            }
        }

        foreach (var job in jobs)
        {
            if (job.State is JobState.Queued or JobState.Downloading)
            {
                refresh |= await TrackActiveAsync(job, history, job.Folder ?? config.Folder, location, now, cancellationToken).ConfigureAwait(false);
            }
            else if (job.State == JobState.Finished)
            {
                refresh |= await TrackFinishedAsync(job, location, now, cancellationToken).ConfigureAwait(false);
            }
        }

        if (refresh && location is not null)
        {
            _libraryIndex.Invalidate();
            _refresher.QueueRefresh(location.Id);
        }
    }

    /// <summary>
    /// Tidies the library folder. While MeTube is idle: moves files that landed in a
    /// merged-away channel folder into the folder it was merged into, and removes the
    /// empty folders yt-dlp leaves in MeTube's temp folder. Always: crops letterboxed
    /// thumbnails. Internal so tests and diagnostics can call it.
    /// </summary>
    internal async Task MaintainAsync(CancellationToken cancellationToken)
    {
        var location = _locator.Resolve();
        if (location is null)
        {
            return;
        }

        await ReconcileDeliveredAsync(location, cancellationToken).ConfigureAwait(false);

        if (await IsMeTubeIdleAsync(cancellationToken).ConfigureAwait(false))
        {
            var moved = _merger.SweepAliases();
            if (moved.Count > 0)
            {
                await _store.RelocateAsync(moved, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                _libraryIndex.Invalidate();
                _refresher.QueueRefresh(location.Id);
            }

            _maintenance.CleanTempFolders();
        }

        await _maintenance.SweepThumbnailsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The hosted service starts before Jellyfin has finished its own startup
        // tasks; give the library manager a moment.
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let the watcher die: an exception escaping a BackgroundService
                // stops it for good, and jobs would hang until the next restart.
                _logger.LogWarning(ex, "MeTube: download watcher pass failed.");
            }

            if (DateTimeOffset.UtcNow - _lastMaintenance >= MaintenanceInterval)
            {
                _lastMaintenance = DateTimeOffset.UtcNow;
                try
                {
                    await MaintainAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MeTube: library maintenance failed.");
                }
            }

            try
            {
                await _wake.WaitAsync(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<bool> TrackActiveAsync(JobRecord job, MeTubeHistory? history, string folder, LibraryLocation? location, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (JobTracker.IsPastDeadline(job, now))
        {
            await SetStateAsync(job, JobState.Failed, j => j.Error = "Timed out waiting for MeTube.", now, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (_submitting.ContainsKey(job.Id))
        {
            // MeTube is still probing the video; it has no history entry yet, and an
            // older entry for the same URL (say, a failed attempt) must not be matched.
            _misses.Remove(job.Id);
            return false;
        }

        if (history is null)
        {
            return false;
        }

        var result = JobTracker.Evaluate(job, history, folder);
        if (!result.Seen)
        {
            var miss = _misses.TryGetValue(job.Id, out var previous) ? (previous.Count + 1, previous.First) : (1, now);
            _misses[job.Id] = miss;
            if (!JobTracker.IsMissing(miss.Item1, miss.Item2, now))
            {
                return false;
            }

            // Gone from MeTube's history (cleared, cancelled, or MeTube restarted
            // without its state). The file may still have landed.
            _misses.Remove(job.Id);
            _libraryIndex.Invalidate();
            var itemId = _libraryIndex.ResolveItemId(job.VideoId, null);
            if (itemId is not null)
            {
                await SetStateAsync(job, JobState.InLibrary, j => j.ItemId = itemId, now, cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (_libraryIndex.TryFindFile(job.VideoId, out _))
            {
                await SetStateAsync(job, JobState.Finished, j => j.FinishedUtc = now, now, cancellationToken).ConfigureAwait(false);
                return true;
            }

            await SetStateAsync(job, JobState.Missing, j => j.Error = "MeTube no longer lists this download.", now, cancellationToken).ConfigureAwait(false);
            return false;
        }

        _misses.Remove(job.Id);
        switch (result.State)
        {
            case JobState.Downloading:
                _store.SetProgress(job.Id, result.Percent ?? 0);
                if (job.State != JobState.Downloading)
                {
                    await SetStateAsync(job, JobState.Downloading, _ => { }, now, cancellationToken).ConfigureAwait(false);
                }

                return false;

            case JobState.Finished:
                var filename = await SettleAsync(result.Filename, location, cancellationToken).ConfigureAwait(false);
                await SetStateAsync(
                    job,
                    JobState.Finished,
                    j =>
                    {
                        j.Filename = filename;
                        j.FinishedUtc = now;
                    },
                    now,
                    cancellationToken).ConfigureAwait(false);
                return true;

            case JobState.Failed:
                await SetStateAsync(job, JobState.Failed, j => j.Error = result.Error, now, cancellationToken).ConfigureAwait(false);
                return false;

            default:
                if (job.State != JobState.Queued)
                {
                    await SetStateAsync(job, JobState.Queued, _ => { }, now, cancellationToken).ConfigureAwait(false);
                }

                return false;
        }
    }

    private async Task<bool> TrackFinishedAsync(JobRecord job, LibraryLocation? location, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var itemId = _libraryIndex.ResolveItemId(job.VideoId, MapPath(job, location));
        if (itemId is not null)
        {
            _rescanned.Remove(job.Id);
            await SetStateAsync(job, JobState.InLibrary, j => j.ItemId = itemId, now, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (JobTracker.IsLibraryWaitOver(job, now))
        {
            _rescanned.Remove(job.Id);
            _logger.LogWarning(
                "MeTube: {VideoId} finished as {File} but did not appear in the library. Check that the library folder is MeTube's '{Folder}' folder.",
                job.VideoId,
                job.Filename,
                job.Folder ?? Plugin.Config.Folder);
            await SetStateAsync(job, JobState.Downloaded, _ => { }, now, cancellationToken).ConfigureAwait(false);
            return false;
        }

        // One more scan if the first one has not found it after a few minutes, for
        // example when the scan was already running when the file arrived.
        return now - (job.FinishedUtc ?? job.UpdatedUtc) > JobTracker.RescanAfter && _rescanned.Add(job.Id);
    }

    /// <summary>
    /// A job that ended as Downloaded, Missing or Failed may still have its video show
    /// up later (a slow scan, a history cleared while MeTube kept going). Give it the
    /// happy ending when it does, so users get their Play button.
    /// </summary>
    private async Task RecheckClosedAsync(IReadOnlyList<JobRecord> all, LibraryLocation? location, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var candidates = all.Where(j =>
                j.State is JobState.Downloaded or JobState.Missing or JobState.Failed
                && now - j.UpdatedUtc < JobTracker.RecheckClosedFor)
            .ToList();

        foreach (var job in candidates)
        {
            var itemId = _libraryIndex.ResolveItemId(job.VideoId, MapPath(job, location));
            if (itemId is not null)
            {
                await SetStateAsync(
                    job,
                    JobState.InLibrary,
                    j =>
                    {
                        j.ItemId = itemId;
                        j.Error = null;
                    },
                    now,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Settles the jobs of videos whose Jellyfin item was just removed: when the file is
    /// gone too (deleted in Jellyfin's own menu, or on disk), the video is no longer in
    /// the library and its jobs become Deleted, which drops them from "Added videos".
    /// A file that is still there (a merge, a rescan) keeps its jobs.
    /// </summary>
    private async Task ForgetRemovedAsync(IReadOnlyList<string> videoIds, LibraryLocation? location, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (videoIds.Count == 0 || location is null || !Directory.Exists(location.Path))
        {
            return;
        }

        _libraryIndex.Invalidate();
        if (_libraryIndex.Count == 0)
        {
            return; // An empty folder is more likely a share that is not readable right now.
        }

        foreach (var videoId in videoIds)
        {
            if (!_libraryIndex.TryFindFile(videoId, out _))
            {
                await _store.MarkDeletedAsync(videoId, now, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Backstop for <see cref="ForgetRemovedAsync"/> (and for videos removed before this
    /// version): delivered jobs whose file is no longer in the library become Deleted,
    /// and an InLibrary job whose item was replaced (a rescan, a merge) gets the new item.
    /// </summary>
    private async Task ReconcileDeliveredAsync(LibraryLocation location, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(location.Path))
        {
            return; // An unmounted pool is not a deleted library.
        }

        var delivered = (await _store.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(j => j.State is JobState.InLibrary or JobState.Downloaded)
            .ToList();
        if (delivered.Count == 0)
        {
            return;
        }

        _libraryIndex.Invalidate();
        var groups = delivered.GroupBy(j => j.VideoId, StringComparer.Ordinal).ToList();
        var missing = groups.Count(g => !_libraryIndex.TryFindFile(g.Key, out _));
        if (missing > 0 && (_libraryIndex.Count == 0 || (missing > 3 && missing * 2 > groups.Count)))
        {
            // Most of the library missing at once looks like a share that is not readable
            // right now, not like deletions; those are caught one by one as they happen.
            _logger.LogWarning("MeTube: {Missing} of {Total} added videos are not in the library folder right now; leaving their jobs alone.", missing, groups.Count);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var group in groups)
        {
            if (!_libraryIndex.TryFindFile(group.Key, out var path))
            {
                await _store.MarkDeletedAsync(group.Key, now, cancellationToken).ConfigureAwait(false);
                continue;
            }

            foreach (var job in group.Where(j => j.State == JobState.InLibrary && (j.ItemId is not { } id || !_libraryIndex.ItemExists(id))))
            {
                if (_libraryIndex.ResolveItemId(job.VideoId, path) is { } itemId)
                {
                    await _store.UpdateAsync(job.Id, j => j.ItemId = itemId, now, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Puts a finished download in its final shape before Jellyfin scans it: into the
    /// kept folder when its channel was merged, with the black bars cropped off its
    /// thumbnail. Returns the (possibly new) file name relative to the library.
    /// </summary>
    private async Task<string?> SettleAsync(string? filename, LibraryLocation? location, CancellationToken cancellationToken)
    {
        try
        {
            filename = _merger.ApplyAlias(filename) ?? filename;
            if (location is not null && PathMapper.TryMap(location.Path, filename, out var path))
            {
                await _maintenance.FixThumbnailAsync(path, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MeTube: could not tidy up {File}.", filename);
        }

        return filename;
    }

    private async Task<bool> IsMeTubeIdleAsync(CancellationToken cancellationToken)
    {
        var url = Plugin.Config.MeTubeUrl;
        if (string.IsNullOrWhiteSpace(url) || !_submitting.IsEmpty)
        {
            return false;
        }

        try
        {
            var history = await _meTube.GetHistoryAsync(url, cancellationToken).ConfigureAwait(false);
            return history.Queue.Count == 0 && history.Pending.Count == 0;
        }
        catch (MeTubeException ex)
        {
            _logger.LogDebug(ex, "MeTube: could not ask MeTube whether it is busy; skipping the folder clean-up.");
            return false;
        }
    }

    private static string? MapPath(JobRecord job, LibraryLocation? location) =>
        location is not null && PathMapper.TryMap(location.Path, job.Filename, out var path) ? path : null;

    private Task<JobRecord?> SetStateAsync(JobRecord job, JobState state, Action<JobRecord> change, DateTimeOffset now, CancellationToken cancellationToken) =>
        _store.UpdateAsync(
            job.Id,
            j =>
            {
                j.State = state;
                change(j);
            },
            now,
            cancellationToken);
}
