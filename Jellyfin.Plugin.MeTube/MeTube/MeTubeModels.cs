using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MeTube.MeTube;

/// <summary>One download as MeTube's /history reports it.</summary>
public sealed class MeTubeEntry
{
    /// <summary>Gets or sets MeTube's id for the download (the YouTube id for YouTube videos).</summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets the download URL.</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the status: pending, queued, scheduled, preparing,
    /// downloading, postprocessing, finished or error.
    /// </summary>
    public string? Status { get; set; }

    /// <summary>Gets or sets the progress percentage while downloading.</summary>
    public double? Percent { get; set; }

    /// <summary>Gets or sets the final file name, relative to the download folder.</summary>
    public string? Filename { get; set; }

    /// <summary>Gets or sets the download folder, relative to MeTube's download directory.</summary>
    public string? Folder { get; set; }

    /// <summary>Gets or sets when MeTube created the entry, in Unix nanoseconds.</summary>
    public long? Timestamp { get; set; }

    /// <summary>Gets or sets MeTube's status message.</summary>
    public string? Msg { get; set; }

    /// <summary>Gets or sets MeTube's error text.</summary>
    public string? Error { get; set; }
}

/// <summary>MeTube's /history: finished downloads, the active queue and pending items.</summary>
public sealed class MeTubeHistory
{
    /// <summary>Gets the finished (or failed) downloads.</summary>
    public List<MeTubeEntry> Done { get; } = new();

    /// <summary>Gets the downloads that are queued or running.</summary>
    public List<MeTubeEntry> Queue { get; } = new();

    /// <summary>Gets the downloads waiting to be started.</summary>
    public List<MeTubeEntry> Pending { get; } = new();
}

/// <summary>How MeTube answered an add request.</summary>
public enum MeTubeAddOutcome
{
    /// <summary>The video was queued.</summary>
    Ok,

    /// <summary>The same URL was already in MeTube's queue.</summary>
    AlreadyQueued,

    /// <summary>MeTube refused or failed.</summary>
    Error
}

/// <summary>
/// MeTube's answer to an add request. <paramref name="ConfigProblem"/> marks answers
/// that point at the plugin or MeTube setup (HTTP 400 for an unknown preset or bad
/// option, server errors) rather than at the video itself.
/// </summary>
public sealed record MeTubeAddResult(MeTubeAddOutcome Outcome, string? Message, bool ConfigProblem = false);

/// <summary>Options sent with every add request.</summary>
public sealed record MeTubeAddOptions(
    string Folder,
    IReadOnlyList<string> Presets,
    string Quality,
    string Format,
    string Codec);

/// <summary>MeTube could not be reached or answered with an unexpected status.</summary>
public sealed class MeTubeException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="MeTubeException"/> class.</summary>
    public MeTubeException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }

    /// <summary>
    /// Gets a value indicating whether MeTube simply did not answer in time. For /add that
    /// usually means it is still probing the video and will queue it anyway.
    /// </summary>
    public bool IsTimeout { get; init; }
}
