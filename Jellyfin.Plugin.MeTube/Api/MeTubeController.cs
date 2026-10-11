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

    private readonly SearchService _search;
    private readonly JobService _jobs;
    private readonly JobStore _store;
    private readonly LibraryIndex _libraryIndex;
    private readonly LibraryLocator _locator;
    private readonly MeTubeClient _meTube;
    private readonly InnerTubeClient _innerTube;
    private readonly UserAccess _access;
    private readonly WebInjectionStatus _injection;

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
        WebInjectionStatus injection)
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
            InjectionEnabled = config.InjectionEnabled
        };
    }

    /// <summary>Searches YouTube.</summary>
    [HttpGet("Search")]
    public async Task<ActionResult<SearchResponseDto>> Search([FromQuery] string? q, [FromQuery] string? continuation, CancellationToken cancellationToken)
    {
        NoStore();
        if (!TryGetCaller(out var userId, out var isAdmin, out var denied))
        {
            return denied;
        }

        var outcome = await _search.SearchAsync(userId, q, continuation, cancellationToken).ConfigureAwait(false);
        if (outcome.Page is null)
        {
            return StatusCode(outcome.StatusCode, new ErrorDto { Error = outcome.Error ?? "Search failed." });
        }

        var config = Plugin.Config;
        var openJobs = (await _store.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(j => j.IsOpen)
            .GroupBy(j => j.VideoId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

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
                ThumbnailUrl = video.ThumbnailUrl
            };

            if (_libraryIndex.TryFindFile(video.VideoId, out var file))
            {
                item.Availability = "InLibrary";
                item.ItemId = _libraryIndex.ResolveItemId(video.VideoId, file);
                _jobs.RefreshIfOrphan(item.ItemId);
            }
            else if (openJobs.TryGetValue(video.VideoId, out var job))
            {
                item.Availability = "InProgress";
                item.Job = ToDto(job, isAdmin);
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

        return new AddResponseDto
        {
            AlreadyInLibrary = outcome.AlreadyInLibrary,
            ItemId = outcome.LibraryItemId,
            Job = outcome.Job is null ? null : ToDto(outcome.Job, isAdmin)
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
        if (!string.IsNullOrWhiteSpace(videoIds))
        {
            var wanted = videoIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(VideoIdParser.IsValidId)
                .Take(StatusJobLimit)
                .ToHashSet(StringComparer.Ordinal);
            return jobs
                .Where(j => wanted.Contains(j.VideoId))
                .GroupBy(j => j.VideoId, StringComparer.Ordinal)
                .Select(g => ToDto(g.First(), isAdmin))
                .ToList();
        }

        return jobs
            .Where(j => isAdmin || j.UserId == userId || (j.RequestedBy?.Contains(userId) ?? false))
            .Take(StatusJobLimit)
            .Select(j => ToDto(j, isAdmin))
            .ToList();
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

    private JobDto ToDto(JobRecord job, bool isAdmin)
    {
        var itemGone = job.State == JobState.InLibrary && job.ItemId is { } itemId && !_libraryIndex.ItemExists(itemId);
        return JobDto.From(job, _store.GetProgress(job.Id), isAdmin, itemGone);
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
