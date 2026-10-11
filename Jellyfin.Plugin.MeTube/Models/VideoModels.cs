using System.Collections.Generic;

namespace Jellyfin.Plugin.MeTube.Models;

/// <summary>One YouTube search result.</summary>
public sealed class VideoResult
{
    /// <summary>Gets or sets the 11-character YouTube video id.</summary>
    public string VideoId { get; set; } = string.Empty;

    /// <summary>Gets or sets the video title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the channel name, when known.</summary>
    public string? Channel { get; set; }

    /// <summary>Gets or sets the channel id, when known.</summary>
    public string? ChannelId { get; set; }

    /// <summary>Gets or sets the duration in seconds; null for live streams or when unknown.</summary>
    public int? DurationSeconds { get; set; }

    /// <summary>Gets or sets the duration as YouTube displays it, for example 12:34.</summary>
    public string? DurationText { get; set; }

    /// <summary>Gets or sets the relative upload date, for example "3 days ago".</summary>
    public string? PublishedText { get; set; }

    /// <summary>Gets or sets the view count text, for example "1.2M views".</summary>
    public string? ViewsText { get; set; }

    /// <summary>Gets or sets a value indicating whether the video is a live stream.</summary>
    public bool IsLive { get; set; }

    /// <summary>Gets or sets a value indicating whether the video is an upcoming premiere or stream.</summary>
    public bool IsUpcoming { get; set; }

    /// <summary>Gets or sets the 16:9 thumbnail URL.</summary>
    public string ThumbnailUrl { get; set; } = string.Empty;
}

/// <summary>One page of search results.</summary>
public sealed class SearchPage
{
    /// <summary>Initializes a new instance of the <see cref="SearchPage"/> class.</summary>
    public SearchPage(IReadOnlyList<VideoResult> items, string? continuation)
    {
        Items = items;
        Continuation = continuation;
    }

    /// <summary>Gets the results on this page.</summary>
    public IReadOnlyList<VideoResult> Items { get; }

    /// <summary>Gets the token for the next page, or null at the end.</summary>
    public string? Continuation { get; }
}

/// <summary>Details of one video, from a search result or a player lookup.</summary>
public sealed class VideoInfo
{
    /// <summary>Gets or sets the video id.</summary>
    public string VideoId { get; set; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the channel name.</summary>
    public string? Channel { get; set; }

    /// <summary>Gets or sets the duration in seconds, when known.</summary>
    public int? DurationSeconds { get; set; }

    /// <summary>Gets or sets a value indicating whether this is a live stream.</summary>
    public bool IsLive { get; set; }

    /// <summary>Gets or sets a value indicating whether this is an upcoming premiere or stream.</summary>
    public bool IsUpcoming { get; set; }

    /// <summary>Gets or sets a value indicating whether YouTube knows the video at all.</summary>
    public bool Available { get; set; } = true;

    /// <summary>Gets or sets why the video is unavailable.</summary>
    public string? UnavailableReason { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the info came from an
    /// authoritative player lookup rather than a search result.
    /// </summary>
    public bool FromPlayer { get; set; }
}
