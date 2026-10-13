using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MeTube.Jobs;
using Jellyfin.Plugin.MeTube.Library;
using Jellyfin.Plugin.MeTube.MeTube;
using Jellyfin.Plugin.MeTube.Models;
using Jellyfin.Plugin.MeTube.Search;
using Jellyfin.Plugin.MeTube.Web;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MeTube.Api;

/// <summary>
/// The plugin's API. Every endpoint needs a signed-in Jellyfin user (Jellyfin has
/// no fallback authorization policy, so this attribute is what protects them on a
/// public server), and every endpoint except Me also needs that user to be an
/// admin or on the allowed list. The only anonymous endpoint serves the client
/// script, which contains no configuration or secrets.
/// </summary>
[ApiController]
[Route("MeTube")]
[Authorize]
public sealed class MeTubeController : ControllerBase
{
    private const int StatusJobLimit = 50;
    private const string RemovedState = "Removed";

    private readonly SearchService _search;
    private readonly JobService _jobs;
    private readonly JobStore _store;
    private readonly LibraryIndex _libraryIndex;
    private readonly LibraryLocator _locator;
    private readonly MeTubeClient _meTube;
    private readonly InnerTubeClient _innerTube;
    private readonly UserAccess _access;
    private readonly WebInjectionStatus _injection;
    private readonly VideoDeleter _deleter;
    private readonly FolderMerger _merger;
    private readonly FolderAliasStore _aliases;
    private readonly LibraryRefresher _refresher;

    /// <summary>Initializes a new instance of the <see cref="MeTubeController"/> class.</summary>
    public MeTubeController(
        SearchService search,
        JobService jobs,
        JobStore store,
        LibraryIndex libraryIndex,
        LibraryLocator locator,
        MeTubeClient meTube,
        InnerTubeClient innerTube,
        UserAccess access,
        WebInjectionStatus injection,
        VideoDeleter deleter,
        FolderMerger merger,
        FolderAliasStore aliases,
        LibraryRefresher refresher)
    {
        _search = search;
        _jobs = jobs;
        _store = store;
        _libraryIndex = libraryIndex;
        _locator = locator;
        _meTube = meTube;
        _innerTube = innerTube;
        _access = access;
        _injection = injection;
        _deleter = deleter;
        _merger = merger;
        _aliases = aliases;
        _refresher = refresher;
    }

    /// <summary>Serves the client script that adds the YouTube menu entry.</summary>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    public ActionResult ClientScript()
    {
        Response.Headers.CacheControl = "no-cache";
        var assembly = typeof(MeTubeController).Assembly;
        var resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".Web.metube.js", StringComparison.OrdinalIgnoreCase));
        using var stream = resource is null ? null : assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Content(reader.ReadToEnd(), "application/javascript; charset=utf-8");
    }

    /// <summary>Tells the client script whether to show the YouTube entry for this user.</summary>
    [HttpGet("Me")]
    public ActionResult<MeDto> Me()
    {
        NoStore();
        var config = Plugin.Config;
        var userId = UserAccess.GetUserId(User);
        var isAdmin = UserAccess.IsAdmin(User);
        var allowed = UserAccess.IsAllowed(userId, isAdmin, config.AllowedUserIds);
        var library = allowed ? _locator.Resolve() : null;
        return new MeDto
        {
            Allowed = allowed,
            IsAdmin = isAdmin,
            Configured = allowed && library is not null && !string.IsNullOrWhiteSpace(config.MeTubeUrl),
            CanPlay = library is not null && _access.CanSeeLibrary(userId, library.Id),
            DailyLimit = config.DailyLimitPerUser,
            MaxDurationMinutes = config.MaxDurationMinutes,
            InjectionEnabled = config.InjectionEnabled,

            // Every signed-in user, allowed or not, sees the library in Jellyfin's menus.
            LibraryId = _locator.Resolve()?.Id
        };
    }

    /// <summary>Searches YouTube.</summary>
    [HttpGet("Search")]
    public async Task<ActionResult<SearchResponseDto>> Search(
        [FromQuery] string? q,
        [FromQuery] string? continuation,
        [FromQuery] string? sort,
        [FromQuery] string? duration,
        [FromQuery] string? date,
        CancellationToken cancellationToken)
    {
        NoStore();
        if (!TryGetCaller(out var userId, out var isAdmin, out var denied))
        {
            return denied;
        }

        var filters = SearchFilters.Parse(sort, duration, date);
        var outcome = await _search.SearchAsync(userId, q, continuation, filters, cancellationToken).ConfigureAwait(false);
        if (outcome.Page is null)
        {
            return StatusCode(outcome.StatusCode, new ErrorDto { Error = outcome.Error ?? "Search failed." });
        }

        var config = Plugin.Config;
        var allJobs = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var openJobs = allJobs
            .Where(j => j.IsOpen)
            .GroupBy(j => j.VideoId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var owners = OwnersByVideo(allJobs);

        var response = new SearchResponseDto { Continuation = outcome.Page.Continuation, Source = outcome.Source };
        foreach (var video in outcome.Page.Items)
        {
            var item = new SearchItemDto
            {
                VideoId = video.VideoId,
                Title = video.Title,
                Channel = video.Channel,
                DurationText = video.DurationText,
                PublishedText = video.PublishedText,
                ViewsText = video.ViewsText,
                DurationSeconds = video.DurationSeconds,
                ViewCount = video.ViewCount,
                AgeSeconds = video.AgeSeconds,
                ThumbnailUrl = video.ThumbnailUrl
            };

            if (_libraryIndex.TryFindFile(video.VideoId, out var file))
            {
                item.Availability = "InLibrary";
                item.ItemId = _libraryIndex.ResolveItemId(video.VideoId, file);
                item.CanDelete = isAdmin || (owners.TryGetValue(video.VideoId, out var owner) && owner == userId);
                _jobs.RefreshIfOrphan(item.ItemId);
            }
            else if (openJobs.TryGetValue(video.VideoId, out var job))
            {
                item.Availability = "InProgress";
                item.Job = ToDto(job, isAdmin, userId, owners);
            }
            else if (video.IsUpcoming)
            {
                item.Availability = "NotAddable";
                item.Reason = "Upcoming";
            }
            else if (video.IsLive)
            {
                item.Availability = "NotAddable";
                item.Reason = "Live";
            }
            else if (!isAdmin && config.MaxDurationMinutes > 0 && video.DurationSeconds > config.MaxDurationMinutes * 60)
            {
                item.Availability = "NotAddable";
                item.Reason = "TooLong";
            }

            response.Items.Add(item);
        }

        return response;
    }

    /// <summary>Adds a video by id or pasted link.</summary>
    [HttpPost("Add")]
    public async Task<ActionResult<AddResponseDto>> Add([FromBody] AddRequestDto request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!TryGetCaller(out var userId, out var isAdmin, out var denied))
        {
            return denied;
        }

        var input = string.IsNullOrWhiteSpace(request?.VideoId) ? request?.Url : request.VideoId;
        if (!VideoIdParser.TryExtract(input, out var videoId))
        {
            return BadRequest(new ErrorDto { Error = "That is not a YouTube video link." });
        }

        var outcome = await _jobs.AddAsync(userId, _access.GetUserName(userId), isAdmin, videoId, cancellationToken).ConfigureAwait(false);
        if (outcome.Error is not null)
        {
            return StatusCode(outcome.StatusCode, new ErrorDto { Error = outcome.Error });
        }

        var owners = outcome.Job is null ? null : OwnersByVideo(await _store.ListAsync(cancellationToken).ConfigureAwait(false));
        return new AddResponseDto
        {
            AlreadyInLibrary = outcome.AlreadyInLibrary,
            ItemId = outcome.LibraryItemId,
            Job = outcome.Job is null ? null : ToDto(outcome.Job, isAdmin, userId, owners)
        };
    }

    /// <summary>
    /// Lists the caller's recent downloads (their own and those they also asked for);
    /// administrators see everyone's. With <paramref name="videoIds"/> (comma-separated)
    /// it instead returns the newest job for each of those videos, whoever added it, so
    /// a search card can follow a download another user started.
    /// </summary>
    [HttpGet("Status")]
    public async Task<ActionResult<List<JobDto>>> Status([FromQuery] string? videoIds, CancellationToken cancellationToken)
    {
        NoStore();
        if (!TryGetCaller(out var userId, out var isAdmin, out var denied))
        {
            return denied;
        }

        var jobs = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var owners = OwnersByVideo(jobs);
        if (!string.IsNullOrWhiteSpace(videoIds))
        {
            var wanted = videoIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(VideoIdParser.IsValidId)
                .Take(StatusJobLimit)
                .ToHashSet(StringComparer.Ordinal);
            return jobs
                .Where(j => wanted.Contains(j.VideoId))
                .GroupBy(j => j.VideoId, StringComparer.Ordinal)
                .Select(g => ToDto(g.First(), isAdmin, userId, owners))
                .ToList();
        }

        // The list leaves out what the user removed from it and videos that are no
        // longer in the library (deleted here, in Jellyfin's own menu, or on disk).
        return jobs
            .Where(j => (isAdmin || j.UserId == userId || (j.RequestedBy?.Contains(userId) ?? false)) && !j.Dismissed && j.State != JobState.Deleted)
            .Select(j => ToDto(j, isAdmin, userId, owners))
            .Where(d => d.State != RemovedState)
            .Take(StatusJobLimit)
            .ToList();
    }

    /// <summary>
    /// Removes entries from the caller's "Added videos" list: failed, lost and removed
    /// downloads (never one that is still on its way or playable). Administrators can
    /// remove anyone's entries; others only their own. With All, every such entry goes.
    /// </summary>
    [HttpPost("Jobs/Dismiss")]
    public async Task<ActionResult<DismissResponseDto>> Dismiss([FromBody] DismissRequestDto request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!TryGetCaller(out var userId, out var isAdmin, out var denied))
        {
            return denied;
        }

        var wanted = (request?.JobIds ?? new List<Guid>()).Take(500).ToHashSet();
        var all = request?.All == true;
        if (!all && wanted.Count == 0)
        {
            return BadRequest(new ErrorDto { Error = "Nothing to remove." });
        }

        var jobs = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var ids = jobs
            .Where(j => (all || wanted.Contains(j.Id)) && CanDismiss(j, isAdmin, userId))
            .Select(j => j.Id)
            .ToList();
        var count = await _store.DismissAsync(ids, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        return new DismissResponseDto { Count = count };
    }

    /// <summary>
    /// Deletes a video from the library: its files, its Jellyfin item and, when empty,
    /// its channel folder. Allowed for the user who added it and for administrators,
    /// independent of Jellyfin's own "allow media deletion" setting, which would let a
    /// user delete everything in the library rather than just their own videos.
    /// </summary>
    [HttpPost("Delete")]
    public async Task<ActionResult> Delete([FromBody] DeleteRequestDto request, CancellationToken cancellationToken)
    {
        NoStore();
        if (!TryGetCaller(out var userId, out var isAdmin, out var denied))
        {
            return denied;
        }

        var videoId = request?.VideoId;
        if (!VideoIdParser.IsValidId(videoId))
        {
            return BadRequest(new ErrorDto { Error = "That is not a YouTube video id." });
        }

        var ownerJob = await _store.FindOwnerJobAsync(videoId, cancellationToken).ConfigureAwait(false);
        if (!isAdmin && ownerJob?.UserId != userId)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorDto { Error = "Only the person who added this video, or an administrator, can delete it." });
        }

        var library = _locator.Resolve();
        var result = _deleter.Delete(videoId, ownerJob?.Filename);
        switch (result.Outcome)
        {
            case DeleteOutcome.NotWritable:
            case DeleteOutcome.Refused:
                return StatusCode(StatusCodes.Status409Conflict, new ErrorDto { Error = result.Message ?? "Could not delete the video." });
            case DeleteOutcome.Unavailable:
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorDto { Error = result.Message ?? "The YouTube library is not available right now." });
            case DeleteOutcome.NotFound:
                // Already gone (deleted in Jellyfin or on disk): just tidy up the jobs.
                await _store.MarkDeletedAsync(videoId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                return StatusCode(StatusCodes.Status404NotFound, new ErrorDto { Error = result.Message ?? "This video is no longer in the library." });
        }

        // Another copy of the same video (say, from a playlist download) may remain:
        // then only the owner's own job closes, and the remaining copy keeps its owner.
        if (_libraryIndex.TryFindFile(videoId, out _) && ownerJob is not null)
        {
            await _store.UpdateAsync(
                ownerJob.Id,
                j =>
                {
                    j.State = JobState.Deleted;
                    j.ItemId = null;
                },
                DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await _store.MarkDeletedAsync(videoId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }

        await ForgetInMeTubeAsync(videoId, result.DeletedPath, library).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Lists the library's channel folders and the merges in force (administrators).</summary>
    [HttpGet("Admin/Folders")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<FoldersResponseDto> Folders()
    {
        NoStore();
        return new FoldersResponseDto
        {
            Folders = _merger.ListFolders()
                .Select(f => new FolderDto { Name = f.Name, VideoCount = f.VideoCount, SampleItemId = f.SampleItemId, ItemId = f.ItemId })
                .ToList(),
            Aliases = _aliases.GetAll()
                .OrderBy(a => a.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(a => new FolderAliasDto { Source = a.Key, Target = _aliases.Resolve(a.Key) ?? a.Value })
                .ToList()
        };
    }

    /// <summary>
    /// Merges channel folders into the one to keep (administrators). Later downloads from
    /// the merged channels go into the kept folder too.
    /// </summary>
    [HttpPost("Admin/Merge")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<MergeResponseDto>> Merge([FromBody] MergeRequestDto request)
    {
        NoStore();
        var folders = request?.Folders ?? new List<string>();
        var target = request?.Target;
        if (string.IsNullOrWhiteSpace(target) || folders.Count < 2 || folders.Count > 500 || !folders.Contains(target, StringComparer.Ordinal))
        {
            return BadRequest(new ErrorDto { Error = "Choose at least two folders, and which one to keep." });
        }

        var result = _merger.Merge(folders, target);
        if (result.Error is not null)
        {
            return BadRequest(new ErrorDto { Error = result.Error });
        }

        // The files have moved; the bookkeeping must follow even if the browser gives up.
        await _store.RelocateAsync(result.Moved, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        var library = _locator.Resolve();
        if (library is not null && (result.MovedFiles > 0 || result.RemovedFolders.Count > 0))
        {
            _refresher.QueueRefresh(library.Id);
        }

        return new MergeResponseDto
        {
            MovedVideos = result.Moved.Count,
            MovedFiles = result.MovedFiles,
            Skipped = result.Skipped,
            RemovedFolders = result.RemovedFolders
        };
    }

    /// <summary>Forgets a merge, so MeTube's downloads stay in that channel's own folder again (administrators).</summary>
    [HttpPost("Admin/Aliases/Remove")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult RemoveAlias([FromBody] AliasRemoveRequestDto request)
    {
        NoStore();
        var source = request?.Source;
        if (!FolderNames.IsSafeName(source))
        {
            return BadRequest(new ErrorDto { Error = "That is not a folder name." });
        }

        return _aliases.Remove(source!) ? NoContent() : NotFound(new ErrorDto { Error = "That folder was not merged." });
    }

    /// <summary>Checks MeTube, YouTube, the library and user access, for the settings page.</summary>
    [HttpGet("Admin/Test")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<TestResultDto>> Test(CancellationToken cancellationToken)
    {
        NoStore();
        var config = Plugin.Config;
        var result = new TestResultDto();

        try
        {
            result.MeTubeDetail = await _meTube.GetVersionAsync(config.MeTubeUrl, cancellationToken).ConfigureAwait(false);
            result.MeTubeOk = true;
            result.MeTubePresets = (await _meTube.GetPresetsAsync(config.MeTubeUrl, cancellationToken).ConfigureAwait(false)).ToList();
            result.MissingPresets = JobService.CurrentOptions().Presets.Where(p => !result.MeTubePresets.Contains(p, StringComparer.Ordinal)).ToList();
        }
        catch (MeTubeException ex)
        {
            result.MeTubeDetail = ex.Message;
        }

        try
        {
            var page = await _innerTube.SearchAsync("me at the zoo", null, VideoInfoService.CurrentSettings(), cancellationToken).ConfigureAwait(false);
            result.SearchOk = page.Items.Count > 0;
            result.SearchDetail = page.Items.Count > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0} results, first: {1}", page.Items.Count, page.Items[0].Title)
                : "YouTube answered, but no results could be read. The MeTube fallback search will be used.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            result.SearchDetail = ex.Message;
        }

        var library = _locator.Resolve();
        if (library is null)
        {
            result.LibraryDetail = string.IsNullOrWhiteSpace(config.LibraryId) ? "No library selected." : "The selected library no longer exists.";
        }
        else
        {
            result.LibraryOk = Directory.Exists(library.Path);
            (result.WritableOk, result.WritableDetail) = _deleter.ProbeWritable();
            result.LibraryDetail = library.Name + " (" + library.Path + ")" + (result.LibraryOk ? string.Empty : " — folder not found inside Jellyfin.");
            foreach (var allowedId in config.AllowedUserIds ?? Array.Empty<Guid>())
            {
                var name = _access.GetUserName(allowedId);
                if (name is null)
                {
                    continue;
                }

                if (!_access.CanSeeLibrary(allowedId, library.Id))
                {
                    result.AccessWarnings.Add(name + " cannot see the " + library.Name + " library, so added videos won't show up for them.");
                }

                if (!_access.HasRemoteAccess(allowedId))
                {
                    result.AccessWarnings.Add(name + " has no remote access; they can only use YouTube search at home.");
                }
            }
        }

        // The settings page itself was loaded through index.html, so a working filter
        // has served at least one patched page by the time anyone presses Test.
        result.InjectionOk = config.InjectionEnabled && _injection.FilterInstalled && _injection.LastError is null && _injection.ServedCount > 0;
        result.InjectionDetail = !config.InjectionEnabled
            ? "Menu entry is switched off."
            : !_injection.FilterInstalled
                ? "The injection filter is not active. Restart Jellyfin."
                : _injection.LastError is not null
                    ? "Last error: " + _injection.LastError
                    : _injection.ServedCount == 0
                        ? "No page with the menu entry has been served since startup. Reload the page; if this stays at 0, another plugin may be answering index.html before MeTube."
                        : string.Format(CultureInfo.InvariantCulture, "Active; served {0} page(s) since startup.", _injection.ServedCount);

        return result;
    }

    private static Dictionary<string, Guid> OwnersByVideo(IReadOnlyList<JobRecord> jobs) => jobs
        .Where(j => j.State is JobState.InLibrary or JobState.Downloaded or JobState.Finished)
        .GroupBy(j => j.VideoId, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.CreatedUtc).First().UserId, StringComparer.Ordinal);

    /// <summary>
    /// Builds the client's view of a job. Delete is offered by the same rule the Delete
    /// endpoint enforces: administrators, or the adder of the video's newest delivered job.
    /// </summary>
    private JobDto ToDto(JobRecord job, bool isAdmin, Guid callerId, IReadOnlyDictionary<string, Guid>? owners)
    {
        var view = job;
        var itemGone = false;
        if (job.State == JobState.InLibrary && job.ItemId is { } itemId && !_libraryIndex.ItemExists(itemId))
        {
            // The item is gone. With the file still there (a rescan or merge replaced
            // the item) point at the new item, or say "not visible yet"; without it, the
            // video was deleted ("Removed").
            if (_libraryIndex.TryFindFile(job.VideoId, out var path))
            {
                view = job.Clone();
                view.ItemId = _libraryIndex.ResolveItemId(job.VideoId, path);
                if (view.ItemId is null)
                {
                    view.State = JobState.Downloaded;
                }
            }
            else
            {
                itemGone = true;
            }
        }

        var canDelete = view.State is JobState.InLibrary or JobState.Downloaded
            && (isAdmin || (owners is not null && owners.TryGetValue(job.VideoId, out var owner) && owner == callerId));
        var dto = JobDto.From(view, _store.GetProgress(job.Id), isAdmin, itemGone, canDelete);
        dto.CanDismiss = !job.Dismissed && (isAdmin || job.UserId == callerId) && (itemGone || IsDismissableState(view.State));
        return dto;
    }

    /// <summary>States a user may clear from their list: nothing to play and nothing on its way.</summary>
    private static bool IsDismissableState(JobState state) =>
        state is JobState.Failed or JobState.Missing or JobState.Downloaded or JobState.Deleted;

    private bool CanDismiss(JobRecord job, bool isAdmin, Guid callerId)
    {
        if (job.Dismissed || job.IsOpen || !(isAdmin || job.UserId == callerId))
        {
            return false;
        }

        if (IsDismissableState(job.State))
        {
            return true;
        }

        // An InLibrary job whose video is gone from the library.
        return job.State == JobState.InLibrary && !_libraryIndex.TryFindFile(job.VideoId, out _);
    }

    /// <summary>
    /// Removes MeTube's history entry for the deleted file, so MeTube's own page does not
    /// list a file that no longer exists. Only an entry for this video with the deleted
    /// file's name is removed. The folder is not compared: a merge may have moved the
    /// file out of the channel folder MeTube put it in.
    /// </summary>
    private async Task ForgetInMeTubeAsync(string videoId, string? deletedPath, LibraryLocation? library)
    {
        var baseUrl = Plugin.Config.MeTubeUrl;
        if (string.IsNullOrWhiteSpace(baseUrl) || deletedPath is null || library is null)
        {
            return;
        }

        try
        {
            var history = await _meTube.GetHistoryAsync(baseUrl, CancellationToken.None).ConfigureAwait(false);
            var urls = history.Done
                .Where(e => e.Url is not null
                    && JobTracker.FolderMatches(e.Folder, Plugin.Config.Folder)
                    && (string.Equals(e.Id, videoId, StringComparison.Ordinal) || (VideoIdParser.TryExtract(e.Url, out var id) && id == videoId))
                    && PathMapper.TryMap(library.Path, e.Filename, out var mapped)
                    && string.Equals(Path.GetFileName(mapped), Path.GetFileName(deletedPath), StringComparison.Ordinal))
                .Select(e => e.Url!)
                .ToList();
            await _meTube.RemoveHistoryAsync(baseUrl, urls, CancellationToken.None).ConfigureAwait(false);
        }
        catch (MeTubeException)
        {
            // Cosmetic only; the file is already gone.
        }
    }

    private bool TryGetCaller(out Guid userId, out bool isAdmin, out ActionResult denied)
    {
        userId = UserAccess.GetUserId(User);
        isAdmin = UserAccess.IsAdmin(User);
        denied = null!;
        if (UserAccess.IsAllowed(userId, isAdmin, Plugin.Config.AllowedUserIds))
        {
            return true;
        }

        denied = StatusCode(StatusCodes.Status403Forbidden, new ErrorDto { Error = "You are not allowed to add YouTube videos." });
        return false;
    }

    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
    }
}
