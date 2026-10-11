using System;
using System.Linq;
using Jellyfin.Plugin.MeTube.MeTube;
using Jellyfin.Plugin.MeTube.Models;
using Jellyfin.Plugin.MeTube.Search;

namespace Jellyfin.Plugin.MeTube.Jobs;

/// <summary>What MeTube currently says about one job.</summary>
public sealed record TrackResult(bool Seen, JobState State, double? Percent, string? Filename, string? Error);

/// <summary>
/// Pure matching and state logic between plugin jobs and MeTube's /history.
/// Kept free of I/O so the tricky cases (a stale finished entry for the same URL,
/// a cleared history, timeouts) are covered by unit tests.
/// </summary>
public static class JobTracker
{
    /// <summary>How far before the job's creation a MeTube entry may have been created and still match.</summary>
    public static readonly TimeSpan MatchSlack = TimeSpan.FromSeconds(60);

    /// <summary>How long a queued or downloading job may take before it is given up.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromHours(12);

    /// <summary>How long a finished download may take to appear in the library.</summary>
    public static readonly TimeSpan LibraryWait = TimeSpan.FromMinutes(10);

    /// <summary>After this long without the item, the library is scanned once more.</summary>
    public static readonly TimeSpan RescanAfter = TimeSpan.FromMinutes(3);

    /// <summary>How many consecutive polls a job may be absent from MeTube before it is "Missing".</summary>
    public const int MissingAfterPolls = 3;

    /// <summary>
    /// The shortest time a job must be absent before it is "Missing". Polls also run on
    /// every Add, so counting polls alone could add up within seconds.
    /// </summary>
    public static readonly TimeSpan MissingAfter = TimeSpan.FromSeconds(30);

    /// <summary>Closed jobs this recent are re-checked for a late arrival in the library.</summary>
    public static readonly TimeSpan RecheckClosedFor = TimeSpan.FromHours(24);

    /// <summary>Converts a time to MeTube's timestamp unit (Unix nanoseconds).</summary>
    public static long ToUnixNanoseconds(DateTimeOffset time) => (time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100L;

    /// <summary>
    /// Finds the MeTube entry that belongs to a job. Active entries win over finished
    /// ones, and an entry created before the job (minus a minute of slack) never
    /// matches: MeTube keeps one finished entry per URL, so after a re-add the old
    /// "finished" entry sits in /history until the new download completes.
    /// </summary>
    public static (MeTubeEntry? Entry, bool FromDone) FindEntry(JobRecord job, MeTubeHistory history, string folder)
    {
        bool Matches(MeTubeEntry entry) =>
            IdMatches(entry, job.VideoId)
            && FolderMatches(entry.Folder, folder)
            && (entry.Timestamp is null || entry.Timestamp >= job.MatchSinceNs);

        var active = history.Queue.FirstOrDefault(Matches) ?? history.Pending.FirstOrDefault(Matches);
        return active is not null ? (active, false) : (history.Done.FirstOrDefault(Matches), true);
    }

    /// <summary>
    /// Translates the matching MeTube entry (if any) into a job state. Only entries in
    /// MeTube's "done" list are final: for a merged download yt-dlp reports "finished"
    /// after each part (video, then audio), so a queue entry can briefly read
    /// "finished" while the audio part has not even started.
    /// </summary>
    public static TrackResult Evaluate(JobRecord job, MeTubeHistory history, string folder)
    {
        var (entry, fromDone) = FindEntry(job, history, folder);
        if (entry is null)
        {
            return new TrackResult(false, job.State, null, null, null);
        }

        var status = entry.Status?.ToLowerInvariant();
        if (fromDone)
        {
            return status == "finished"
                ? new TrackResult(true, JobState.Finished, 100, entry.Filename, null)
                : new TrackResult(true, JobState.Failed, null, null, FirstNonEmpty(entry.Msg, entry.Error, "MeTube could not download the video."));
        }

        return status switch
        {
            "finished" => new TrackResult(true, JobState.Downloading, 100, null, null),
            "downloading" or "postprocessing" or "error" => new TrackResult(true, JobState.Downloading, entry.Percent, null, null),
            _ => new TrackResult(true, JobState.Queued, null, null, null)
        };
    }

    /// <summary>Returns true when a queued or downloading job has run past the deadline.</summary>
    public static bool IsPastDeadline(JobRecord job, DateTimeOffset now) =>
        job.State is JobState.Queued or JobState.Downloading && now - job.CreatedUtc > Deadline;

    /// <summary>Returns true when a job has been absent from MeTube long enough to call it missing.</summary>
    public static bool IsMissing(int misses, DateTimeOffset firstMiss, DateTimeOffset now) =>
        misses >= MissingAfterPolls && now - firstMiss >= MissingAfter;

    /// <summary>Returns true when a finished job has waited too long for the library.</summary>
    public static bool IsLibraryWaitOver(JobRecord job, DateTimeOffset now) =>
        job.State == JobState.Finished && now - (job.FinishedUtc ?? job.UpdatedUtc) > LibraryWait;

    private static bool IdMatches(MeTubeEntry entry, string videoId)
    {
        if (string.Equals(entry.Id, videoId, StringComparison.Ordinal))
        {
            return true;
        }

        return VideoIdParser.TryExtract(entry.Url, out var fromUrl) && fromUrl == videoId;
    }

    /// <summary>Returns true when a MeTube entry's folder is the given folder (ignoring slashes and case).</summary>
    internal static bool FolderMatches(string? entryFolder, string folder) =>
        string.Equals(NormalizeFolder(entryFolder), NormalizeFolder(folder), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeFolder(string? folder) => (folder ?? string.Empty).Trim().Trim('/', '\\');

    private static string FirstNonEmpty(params string?[] values) => values.First(v => !string.IsNullOrWhiteSpace(v))!;
}
