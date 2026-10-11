using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MeTube.Models;
using Jellyfin.Plugin.MeTube.Search;

namespace Jellyfin.Plugin.MeTube.Api;

/// <summary>What the client script needs to know about the caller.</summary>
public sealed class MeDto
{
    /// <summary>Gets or sets a value indicating whether the caller may search and add.</summary>
    public bool Allowed { get; set; }

    /// <summary>Gets or sets a value indicating whether the caller is an administrator.</summary>
    public bool IsAdmin { get; set; }

    /// <summary>Gets or sets a value indicating whether the caller can see the YouTube library.</summary>
    public bool CanPlay { get; set; }

    /// <summary>Gets or sets a value indicating whether MeTube and the library are configured.</summary>
    public bool Configured { get; set; }

    /// <summary>Gets or sets the most a non-admin may add per 24 hours (0 = no limit).</summary>
    public int DailyLimit { get; set; }

    /// <summary>Gets or sets the longest video a non-admin may add, in minutes (0 = no limit).</summary>
    public int MaxDurationMinutes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the menu entries should be shown. When an
    /// admin switches injection off, the script may still be loaded by the settings
    /// page's "Open YouTube search", and must then leave the menus alone.
    /// </summary>
    public bool InjectionEnabled { get; set; }
}

/// <summary>A tracked download, as the client script shows it.</summary>
public sealed class JobDto
{
    /// <summary>Gets or sets the job id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the video id.</summary>
    public string VideoId { get; set; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the channel.</summary>
    public string? Channel { get; set; }

    /// <summary>Gets or sets the state name.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Gets or sets the download progress, while downloading.</summary>
    public double? Percent { get; set; }

    /// <summary>Gets or sets the error message.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets the Jellyfin item id, once in the library.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>Gets or sets the thumbnail URL.</summary>
    public string ThumbnailUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets who added it (admins only).</summary>
    public string? UserName { get; set; }

    /// <summary>Gets or sets when it was added.</summary>
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>
    /// Builds the DTO. <paramref name="itemGone"/> reports an InLibrary job whose item was
    /// deleted from Jellyfin since, as the extra state "Removed", so the client offers
    /// Add again instead of a Play button that leads nowhere.
    /// </summary>
    public static JobDto From(JobRecord job, double? percent, bool includeUser, bool itemGone = false) => new()
    {
        Id = job.Id,
        VideoId = job.VideoId,
        Title = job.Title,
        Channel = job.Channel,
        State = itemGone ? "Removed" : job.State.ToString(),
        Percent = job.State == JobState.Downloading ? percent : null,
        Error = job.Error,
        ItemId = itemGone ? null : job.ItemId,
        ThumbnailUrl = InnerTubeParser.ThumbnailUrl(job.VideoId),
        UserName = includeUser ? job.UserName : null,
        CreatedUtc = job.CreatedUtc
    };
}

/// <summary>One search result with its status for this user.</summary>
public sealed class SearchItemDto
{
    /// <summary>Gets or sets the video id.</summary>
    public string VideoId { get; set; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the channel.</summary>
    public string? Channel { get; set; }

    /// <summary>Gets or sets the duration text.</summary>
    public string? DurationText { get; set; }

    /// <summary>Gets or sets the relative upload date.</summary>
    public string? PublishedText { get; set; }

    /// <summary>Gets or sets the view count text.</summary>
    public string? ViewsText { get; set; }

    /// <summary>Gets or sets the thumbnail URL.</summary>
    public string ThumbnailUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets Addable, InLibrary, InProgress or NotAddable.</summary>
    public string Availability { get; set; } = "Addable";

    /// <summary>Gets or sets why the video cannot be added.</summary>
    public string? Reason { get; set; }

    /// <summary>Gets or sets the Jellyfin item id when the video is in the library and scanned.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>Gets or sets the open job for this video.</summary>
    public JobDto? Job { get; set; }
}

/// <summary>A page of search results.</summary>
public sealed class SearchResponseDto
{
    /// <summary>Gets or sets the results.</summary>
    public List<SearchItemDto> Items { get; set; } = new();

    /// <summary>Gets or sets the next-page token.</summary>
    public string? Continuation { get; set; }

    /// <summary>Gets or sets where the results came from: youtube or metube.</summary>
    public string? Source { get; set; }
}

/// <summary>Body of an Add request: a video id or a pasted link.</summary>
public sealed class AddRequestDto
{
    /// <summary>Gets or sets the video id.</summary>
    public string? VideoId { get; set; }

    /// <summary>Gets or sets a pasted YouTube link.</summary>
    public string? Url { get; set; }
}

/// <summary>Answer to an Add request.</summary>
public sealed class AddResponseDto
{
    /// <summary>Gets or sets the job tracking the download.</summary>
    public JobDto? Job { get; set; }

    /// <summary>Gets or sets a value indicating whether the video was already in the library.</summary>
    public bool AlreadyInLibrary { get; set; }

    /// <summary>Gets or sets the library item id when already there.</summary>
    public Guid? ItemId { get; set; }
}

/// <summary>An error answer.</summary>
public sealed class ErrorDto
{
    /// <summary>Gets or sets the message for the user.</summary>
    public string Error { get; set; } = string.Empty;
}

/// <summary>Answer to the admin connection test.</summary>
public sealed class TestResultDto
{
    /// <summary>Gets or sets a value indicating whether MeTube answered.</summary>
    public bool MeTubeOk { get; set; }

    /// <summary>Gets or sets MeTube's version or the error.</summary>
    public string? MeTubeDetail { get; set; }

    /// <summary>Gets or sets the presets MeTube knows.</summary>
    public List<string> MeTubePresets { get; set; } = new();

    /// <summary>Gets or sets configured presets that MeTube does not know.</summary>
    public List<string> MissingPresets { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether a YouTube search worked.</summary>
    public bool SearchOk { get; set; }

    /// <summary>Gets or sets the first search result, or the error.</summary>
    public string? SearchDetail { get; set; }

    /// <summary>Gets or sets a value indicating whether the library is configured and found.</summary>
    public bool LibraryOk { get; set; }

    /// <summary>Gets or sets the library name and folder, or the problem.</summary>
    public string? LibraryDetail { get; set; }

    /// <summary>Gets or sets allowed users who cannot see the library or connect remotely.</summary>
    public List<string> AccessWarnings { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether the menu-entry injection is active.</summary>
    public bool InjectionOk { get; set; }

    /// <summary>Gets or sets the injection status text.</summary>
    public string? InjectionDetail { get; set; }
}
