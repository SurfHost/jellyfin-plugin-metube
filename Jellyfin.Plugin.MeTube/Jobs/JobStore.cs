using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MeTube.Library;
using Jellyfin.Plugin.MeTube.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MeTube.Jobs;

/// <summary>Outcome of reserving a job slot for an Add request.</summary>
public enum ReserveOutcome
{
    /// <summary>A new job was created.</summary>
    Created,

    /// <summary>The video already has an open job; that job is returned.</summary>
    Existing,

    /// <summary>The user reached the daily limit.</summary>
    LimitReached
}

/// <summary>
/// JSON-backed store of Add requests. One lock guards every read-modify-write, so
/// the daily-limit check, the one-open-job-per-video check and the insert are a
/// single step and two quick clicks cannot both get through. Download progress
/// changes every second, so it lives only in memory; the file is written only
/// when a job changes state.
/// </summary>
public sealed class JobStore
{
    /// <summary>Closed jobs older than this are pruned.</summary>
    public static readonly TimeSpan KeepClosedFor = TimeSpan.FromDays(30);

    /// <summary>The most closed jobs (failed, missing, deleted...) kept on disk.</summary>
    public const int MaxJobs = 500;

    /// <summary>
    /// The most jobs kept on disk in total. Ownership records are normally kept for as
    /// long as their video is in the library; this only bounds a store that somehow
    /// keeps growing.
    /// </summary>
    public const int HardCap = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly string _directory;
    private readonly string _path;
    private readonly ILogger<JobStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, double> _progress = new();
    private List<JobRecord>? _jobs;

    /// <summary>Initializes a new instance of the <see cref="JobStore"/> class.</summary>
    public JobStore(IApplicationPaths applicationPaths, ILogger<JobStore> logger)
        : this(Path.Combine(applicationPaths.DataPath, "metube"), logger)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="JobStore"/> class at an explicit folder (tests).</summary>
    public JobStore(string directory, ILogger<JobStore> logger)
    {
        _directory = directory;
        _path = Path.Combine(directory, "jobs.json");
        _logger = logger;
    }

    /// <summary>Returns copies of all jobs, newest first.</summary>
    public async Task<IReadOnlyList<JobRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Load().OrderByDescending(j => j.CreatedUtc).Select(j => j.Clone()).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns a copy of the open job for a video, if any, and records the caller as a
    /// requester when the job belongs to someone else, so they can follow it too.
    /// </summary>
    public async Task<JobRecord?> AttachToOpenAsync(string videoId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = Load();
            var open = jobs.FirstOrDefault(j => j.IsOpen && j.VideoId == videoId);
            if (open is null)
            {
                return null;
            }

            if (AddRequester(open, userId))
            {
                await SaveAsync(jobs, now).ConfigureAwait(false);
            }

            return open.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Creates the job unless the video already has an open job or the user hit the
    /// daily limit. Returns the created or existing job.
    /// </summary>
    public async Task<(ReserveOutcome Outcome, JobRecord? Job)> ReserveAsync(
        JobRecord candidate,
        int dailyLimit,
        bool exemptFromLimit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = Load();
            var open = jobs.FirstOrDefault(j => j.IsOpen && j.VideoId == candidate.VideoId);
            if (open is not null)
            {
                if (AddRequester(open, candidate.UserId))
                {
                    await SaveAsync(jobs, now).ConfigureAwait(false);
                }

                return (ReserveOutcome.Existing, open.Clone());
            }

            if (!exemptFromLimit && dailyLimit > 0)
            {
                var since = now - TimeSpan.FromHours(24);
                var count = jobs.Count(j => j.UserId == candidate.UserId && j.CreatedUtc > since);
                if (count >= dailyLimit)
                {
                    return (ReserveOutcome.LimitReached, null);
                }
            }

            var job = candidate.Clone();
            if (job.Id == Guid.Empty)
            {
                job.Id = Guid.NewGuid();
            }

            job.CreatedUtc = now;
            job.UpdatedUtc = now;
            jobs.Add(job);
            try
            {
                await SaveAsync(jobs, now).ConfigureAwait(false);
            }
            catch
            {
                // Never keep a reservation that is not on disk and was never sent to
                // MeTube: it would block re-adds and count against the daily limit.
                jobs.Remove(job);
                throw;
            }

            return (ReserveOutcome.Created, job.Clone());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Applies a change to one job and saves. Returns the updated copy, or null if it is gone.</summary>
    public async Task<JobRecord?> UpdateAsync(Guid id, Action<JobRecord> change, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = Load();
            var job = jobs.FirstOrDefault(j => j.Id == id);
            if (job is null)
            {
                return null;
            }

            var before = job.State;
            change(job);
            job.UpdatedUtc = now;
            if (!job.IsOpen)
            {
                _progress.TryRemove(id, out _);
            }

            await SaveAsync(jobs, now).ConfigureAwait(false);
            if (before != job.State)
            {
                _logger.LogInformation("MeTube: {VideoId} {Before} -> {After}.", job.VideoId, before, job.State);
            }

            return job.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes a job, used when MeTube refused the Add so it does not count against the limit.</summary>
    public async Task RemoveAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = Load();
            if (jobs.RemoveAll(j => j.Id == id) > 0)
            {
                _progress.TryRemove(id, out _);
                await SaveAsync(jobs, now).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Records in-memory download progress.</summary>
    public void SetProgress(Guid id, double percent) => _progress[id] = Math.Clamp(percent, 0, 100);

    /// <summary>Reads in-memory download progress.</summary>
    public double? GetProgress(Guid id) => _progress.TryGetValue(id, out var percent) ? percent : null;

    /// <summary>
    /// Returns the user who added a video that is (or was meant to be) in the library:
    /// the adder of the newest job that delivered it. Null when the plugin never added it
    /// (downloaded through MeTube's own page, for example); then only admins may delete.
    /// </summary>
    public async Task<Guid?> FindOwnerAsync(string videoId, CancellationToken cancellationToken = default) =>
        (await FindOwnerJobAsync(videoId, cancellationToken).ConfigureAwait(false))?.UserId;

    /// <summary>Returns a copy of the job that makes its adder the owner of a video (see <see cref="FindOwnerAsync"/>).</summary>
    public async Task<JobRecord?> FindOwnerJobAsync(string videoId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Load()
                .Where(j => j.VideoId == videoId && j.State is JobState.InLibrary or JobState.Downloaded or JobState.Finished)
                .OrderByDescending(j => j.CreatedUtc)
                .FirstOrDefault()
                ?.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Marks every delivered job for a video as deleted, so nothing offers Play for it any more.</summary>
    public async Task MarkDeletedAsync(string videoId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = Load();
            var changed = false;
            foreach (var job in jobs.Where(j => j.VideoId == videoId && j.State is JobState.InLibrary or JobState.Downloaded or JobState.Finished))
            {
                job.State = JobState.Deleted;
                job.ItemId = null;
                job.UpdatedUtc = now;
                _progress.TryRemove(job.Id, out _);
                changed = true;
            }

            if (changed)
            {
                await SaveAsync(jobs, now).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Hides jobs from the "Added videos" list. Returns how many changed.</summary>
    public async Task<int> DismissAsync(IReadOnlyCollection<Guid> ids, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = Load();
            var changed = 0;
            foreach (var job in jobs.Where(j => !j.Dismissed && !j.IsOpen && ids.Contains(j.Id)))
            {
                job.Dismissed = true;
                changed++;
            }

            if (changed > 0)
            {
                await SaveAsync(jobs, now).ConfigureAwait(false);
            }

            return changed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Records that videos moved to another folder (a merge). Their Jellyfin items are
    /// replaced by new ones at the new place, so delivered jobs go back to Finished and
    /// the watcher picks up the new item, which brings back the Play button.
    /// </summary>
    public async Task<int> RelocateAsync(IReadOnlyCollection<MovedVideo> moves, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (moves.Count == 0)
        {
            return 0;
        }

        var byVideo = moves.GroupBy(m => m.VideoId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().RelativePath, StringComparer.Ordinal);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = Load();
            var changed = 0;
            foreach (var job in jobs.Where(j => j.State is JobState.InLibrary or JobState.Downloaded or JobState.Finished))
            {
                if (!byVideo.TryGetValue(job.VideoId, out var path))
                {
                    continue;
                }

                job.Filename = path;
                job.ItemId = null;
                job.State = JobState.Finished;
                job.FinishedUtc = now;
                job.UpdatedUtc = now;
                changed++;
            }

            if (changed > 0)
            {
                await SaveAsync(jobs, now).ConfigureAwait(false);
            }

            return changed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Drops closed jobs older than 30 days and keeps at most <see cref="MaxJobs"/> of
    /// them. Jobs whose video is (or should be) in the library are kept: they record who
    /// added it, which is what decides who may delete it. They do not count toward the
    /// cap, so a large library never pushes fresh failures (which count toward the daily
    /// limit) out of the store.
    /// </summary>
    internal static void Prune(List<JobRecord> jobs, DateTimeOffset now)
    {
        static bool Prunable(JobRecord j) => !j.IsOpen && j.State is not (JobState.InLibrary or JobState.Downloaded);

        jobs.RemoveAll(j => Prunable(j) && now - j.UpdatedUtc > KeepClosedFor);

        var prunable = jobs.Where(Prunable).OrderBy(j => j.UpdatedUtc).ToList();
        if (prunable.Count > MaxJobs)
        {
            var drop = prunable.Take(prunable.Count - MaxJobs).ToHashSet();
            jobs.RemoveAll(drop.Contains);
        }

        if (jobs.Count > HardCap)
        {
            var drop = jobs.Where(j => !j.IsOpen).OrderBy(j => j.UpdatedUtc).Take(jobs.Count - HardCap).ToHashSet();
            jobs.RemoveAll(drop.Contains);
        }
    }

    private List<JobRecord> Load()
    {
        if (_jobs is not null)
        {
            return _jobs;
        }

        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                _jobs = JsonSerializer.Deserialize<List<JobRecord>>(json, JsonOptions) ?? new List<JobRecord>();
                return _jobs;
            }
        }
        catch (Exception ex)
        {
            // A corrupt store must not take the feature down. Keep the bad file for
            // inspection and start empty.
            _logger.LogError(ex, "MeTube: could not read {Path}; starting with an empty job list.", _path);
            TryBackupCorrupt();
        }

        _jobs = new List<JobRecord>();
        return _jobs;
    }

    // Deliberately not cancellable: the caller's token has already bounded the wait for
    // the lock, and abandoning a write half-way would only leave memory and disk apart.
    private async Task SaveAsync(List<JobRecord> jobs, DateTimeOffset now)
    {
        Prune(jobs, now);
        Directory.CreateDirectory(_directory);
        var temp = _path + ".tmp";
        var json = JsonSerializer.Serialize(jobs, JsonOptions);
        await File.WriteAllTextAsync(temp, json, CancellationToken.None).ConfigureAwait(false);
        File.Move(temp, _path, overwrite: true);
    }

    private static bool AddRequester(JobRecord job, Guid userId)
    {
        job.RequestedBy ??= new List<Guid>();
        if (userId == Guid.Empty || job.UserId == userId || job.RequestedBy.Contains(userId))
        {
            return false;
        }

        job.RequestedBy.Add(userId);
        return true;
    }

    private void TryBackupCorrupt()
    {
        try
        {
            File.Move(_path, _path + ".corrupt", overwrite: true);
        }
        catch (Exception)
        {
            // Best effort only.
        }
    }
}
