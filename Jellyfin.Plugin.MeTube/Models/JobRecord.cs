using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MeTube.Models;

/// <summary>Where a download request is in its life.</summary>
public enum JobState
{
    /// <summary>MeTube accepted the video and has not started downloading it.</summary>
    Queued = 0,

    /// <summary>MeTube is downloading or post-processing.</summary>
    Downloading = 1,

    /// <summary>MeTube finished; waiting for the file to show up in the Jellyfin library.</summary>
    Finished = 2,

    /// <summary>The video is in the Jellyfin library.</summary>
    InLibrary = 3,

    /// <summary>The download finished, but Jellyfin did not pick the file up in time.</summary>
    Downloaded = 4,

    /// <summary>MeTube reported an error.</summary>
    Failed = 5,

    /// <summary>The request vanished from MeTube (history cleared or download cancelled).</summary>
    Missing = 6,

    /// <summary>The video was deleted from the library through the plugin.</summary>
    Deleted = 7
}

/// <summary>One "Add" request, tracked until the video is in the library.</summary>
public sealed class JobRecord
{
    /// <summary>Gets or sets the job id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the YouTube video id.</summary>
    public string VideoId { get; set; } = string.Empty;

    /// <summary>Gets or sets the video title at the time it was added.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the channel name at the time it was added.</summary>
    public string? Channel { get; set; }

    /// <summary>Gets or sets the duration in seconds, when known.</summary>
    public int? DurationSeconds { get; set; }

    /// <summary>Gets or sets the Jellyfin user who added it.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the user name who added it, for the admin view.</summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Gets or sets other users who pressed Add while this job was already open. They
    /// see it in their own status list so they can follow it to Play.
    /// </summary>
    public List<Guid> RequestedBy { get; set; } = new();

    /// <summary>
    /// Gets or sets the MeTube folder the video was sent to. Matching uses this, not the
    /// current setting, so changing the folder mid-download does not orphan the job.
    /// </summary>
    public string? Folder { get; set; }

    /// <summary>Gets or sets the current state.</summary>
    public JobState State { get; set; }

    /// <summary>Gets or sets the error message for <see cref="JobState.Failed"/>.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets the file name MeTube reported, relative to its download folder.</summary>
    public string? Filename { get; set; }

    /// <summary>Gets or sets the Jellyfin item id once the video is in the library.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>Gets or sets when the job was created.</summary>
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>Gets or sets when the job last changed state.</summary>
    public DateTimeOffset UpdatedUtc { get; set; }

    /// <summary>Gets or sets when MeTube finished the download.</summary>
    public DateTimeOffset? FinishedUtc { get; set; }

    /// <summary>
    /// Gets or sets the lowest MeTube entry timestamp (Unix nanoseconds) that
    /// may belong to this job. MeTube keeps one finished entry per URL until a
    /// new download of the same URL completes, so without this lower bound a
    /// re-add would match the old, already-finished entry immediately.
    /// </summary>
    public long MatchSinceNs { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user removed this job from their
    /// "Added videos" list. The record stays: it still counts toward the daily limit and,
    /// for a video in the library, still says who may delete it.
    /// </summary>
    public bool Dismissed { get; set; }

    /// <summary>Gets a value indicating whether the job still needs watching.</summary>
    public bool IsOpen => State is JobState.Queued or JobState.Downloading or JobState.Finished;

    /// <summary>Creates a copy that callers can read without racing the store.</summary>
    public JobRecord Clone()
    {
        var copy = (JobRecord)MemberwiseClone();
        copy.RequestedBy = new List<Guid>(RequestedBy ?? new List<Guid>());
        return copy;
    }
}
