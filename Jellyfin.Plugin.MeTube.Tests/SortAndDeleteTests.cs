using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.MeTube.Jobs;
using Jellyfin.Plugin.MeTube.Library;
using Jellyfin.Plugin.MeTube.Models;
using Jellyfin.Plugin.MeTube.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MeTube.Tests;

public class SearchFiltersTests
{
    // Values checked live against YouTube on 2026-10-11.
    [Theory]
    [InlineData("relevance", "any", "any", "EgIQAfABAQ==")]
    [InlineData("popularity", "any", "any", "CAMSAhAB8AEB")]
    [InlineData("relevance", "short", "any", "EgQQARgE8AEB")]
    [InlineData("relevance", "medium", "any", "EgQQARgF8AEB")]
    [InlineData("relevance", "long", "any", "EgQQARgC8AEB")]
    [InlineData("popularity", "medium", "any", "CAMSBBABGAXwAQE=")]
    [InlineData("relevance", "any", "today", "EgQIAhAB8AEB")]
    [InlineData("relevance", "medium", "week", "EgYIAxABGAXwAQE=")]
    [InlineData("popularity", "medium", "week", "CAMSBggDEAEYBfABAQ==")]
    public void EncodesTheParamsYouTubeAccepts(string sort, string duration, string date, string expected)
    {
        Assert.Equal(expected, SearchFilters.Parse(sort, duration, date).ToParams());
    }

    [Fact]
    public void TheDefaultIsTheOldVideosOnlyFilter()
    {
        Assert.Equal("EgIQAfABAQ==", SearchFilters.Default.ToParams());
        Assert.True(SearchFilters.Parse(null, null, null).IsDefault);
    }

    [Theory]
    [InlineData("date", "any", "any")]
    [InlineData("rating", "any", "any")]
    [InlineData("relevance", "9", "any")]
    [InlineData("relevance", "any", "decade")]
    [InlineData("<script>", "short\"", "x")]
    public void IgnoresUnknownValues(string sort, string duration, string date)
    {
        var filters = SearchFilters.Parse(sort, duration, date);
        Assert.Contains(filters.Sort, new[] { "relevance", "popularity" });
        Assert.Contains(filters.Duration, new[] { "any", "short", "medium", "long" });
        Assert.Contains(filters.Date, new[] { "any", "today", "week", "month", "year" });
    }

    [Fact]
    public void ValuesAreCaseInsensitive()
    {
        Assert.Equal(new SearchFilters("popularity", "long", "year"), SearchFilters.Parse("Popularity", "LONG", "Year"));
    }
}

public class SortFieldTests
{
    [Theory]
    [InlineData("442,807,905 views", 442807905L)]
    [InlineData("1.101.821 weergaven", 1101821L)]
    [InlineData("1 view", 1L)]
    [InlineData("No views", 0L)]
    public void ReadsExactViewCounts(string text, long expected)
    {
        Assert.Equal(expected, InnerTubeParser.ParseViewCount(text));
    }

    [Theory]
    [InlineData("442M views")]
    [InlineData("1.2K views")]
    [InlineData("1,1 mln. weergaven")]
    [InlineData("")]
    [InlineData(null)]
    public void RefusesRoundedOrMissingCounts(string? text)
    {
        Assert.Null(InnerTubeParser.ParseViewCount(text));
    }

    [Theory]
    [InlineData("6 minutes ago", 360L)]
    [InlineData("1 hour ago", 3600L)]
    [InlineData("3 days ago", 3L * 86400)]
    [InlineData("2 weeks ago", 14L * 86400)]
    [InlineData("1 month ago", 30L * 86400)]
    [InlineData("21 years ago", 21L * 365 * 86400)]
    [InlineData("Streamed 1 year ago", 365L * 86400)]
    [InlineData("16 jaar geleden", 16L * 365 * 86400)]
    [InlineData("3 dagen geleden", 3L * 86400)]
    [InlineData("1 dag geleden", 86400L)]
    [InlineData("1 week geleden", 7L * 86400)]
    [InlineData("5 maanden geleden", 150L * 86400)]
    [InlineData("1 maand geleden", 30L * 86400)]
    [InlineData("2 uur geleden", 7200L)]
    [InlineData("1 minuut geleden", 60L)]
    [InlineData("12 minuten geleden", 720L)]
    [InlineData("Gestreamd 1 jaar geleden", 365L * 86400)]
    public void ReadsRelativeDates(string text, long seconds)
    {
        Assert.Equal(seconds, InnerTubeParser.ParseAge(text));
    }

    [Theory]
    [InlineData("vor 3 Tagen")]
    [InlineData("yesterday")]
    [InlineData(null)]
    public void UnknownDatesSortLast(string? text)
    {
        Assert.Null(InnerTubeParser.ParseAge(text));
    }

    [Fact]
    public void FillsTheSortFieldsFromARealSearch()
    {
        using var doc = FixtureFile.Json("search_me_at_the_zoo.json");
        var first = InnerTubeParser.ParseSearch(doc.RootElement).Items[0];

        Assert.True(first.ViewCount > 400_000_000);
        Assert.True(first.AgeSeconds > 20L * 365 * 86400);
    }
}

public class VideoDeleterRulesTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "lib", "youtube");

    [Fact]
    public void AcceptsAVideoInsideTheLibraryWithTheRightId()
    {
        Assert.True(VideoDeleter.IsSafeTarget(Root, Path.Combine(Root, "jawed", "Me at the zoo [jNQXAC9IVRw].mkv"), "jNQXAC9IVRw"));
    }

    [Fact]
    public void RefusesAnotherVideoId()
    {
        Assert.False(VideoDeleter.IsSafeTarget(Root, Path.Combine(Root, "jawed", "Other [aaaaaaaaaaa].mkv"), "jNQXAC9IVRw"));
    }

    [Fact]
    public void RefusesFilesOutsideTheLibrary()
    {
        var outside = Path.Combine(Path.GetTempPath(), "lib", "movies", "Me at the zoo [jNQXAC9IVRw].mkv");
        var sibling = Path.Combine(Path.GetTempPath(), "lib", "youtube2", "Me at the zoo [jNQXAC9IVRw].mkv");
        var escape = Path.Combine(Root, "..", "movies", "Me at the zoo [jNQXAC9IVRw].mkv");

        Assert.False(VideoDeleter.IsSafeTarget(Root, outside, "jNQXAC9IVRw"));
        Assert.False(VideoDeleter.IsSafeTarget(Root, sibling, "jNQXAC9IVRw"));
        Assert.False(VideoDeleter.IsSafeTarget(Root, escape, "jNQXAC9IVRw"));
    }

    [Theory]
    [InlineData("Me at the zoo [jNQXAC9IVRw].jpg", true)]
    [InlineData("Me at the zoo [jNQXAC9IVRw].nl.vtt", true)]
    [InlineData("Me at the zoo [jNQXAC9IVRw].info.json", true)]
    [InlineData("Me at the zoo [jNQXAC9IVRw]-poster.jpg", true)]
    [InlineData("Me at the zoo [jNQXAC9IVRw] part 2 [bbbbbbbbbbb].mkv", false)]
    [InlineData("poster.jpg", false)]
    [InlineData("Me at the zoo [jNQXAC9IVRw]", false)]
    public void OnlyTouchesTheVideosOwnFiles(string name, bool own)
    {
        Assert.Equal(own, VideoDeleter.IsOwnFile("Me at the zoo [jNQXAC9IVRw]", name));
    }
}

public sealed class OwnershipTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "metube-own-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private JobStore NewStore() => new(_dir, NullLogger<JobStore>.Instance);

    [Fact]
    public async Task TheAdderOfTheDeliveredJobOwnsTheVideo()
    {
        var alice = Guid.NewGuid();
        var store = NewStore();
        var (_, job) = await store.ReserveAsync(new JobRecord { VideoId = "aaaaaaaaaaa", UserId = alice, State = JobState.Queued }, 25, false, Now);

        Assert.Null(await store.FindOwnerAsync("aaaaaaaaaaa"));

        await store.UpdateAsync(job!.Id, j => j.State = JobState.InLibrary, Now);
        Assert.Equal(alice, await store.FindOwnerAsync("aaaaaaaaaaa"));
        Assert.Null(await store.FindOwnerAsync("bbbbbbbbbbb"));
    }

    [Fact]
    public async Task DeletingMarksDeliveredJobsAndEndsOwnership()
    {
        var store = NewStore();
        var (_, job) = await store.ReserveAsync(new JobRecord { VideoId = "aaaaaaaaaaa", UserId = Guid.NewGuid(), State = JobState.Queued }, 25, false, Now);
        await store.UpdateAsync(job!.Id, j =>
        {
            j.State = JobState.InLibrary;
            j.ItemId = Guid.NewGuid();
        }, Now);

        await store.MarkDeletedAsync("aaaaaaaaaaa", Now);

        var stored = Assert.Single(await NewStore().ListAsync());
        Assert.Equal(JobState.Deleted, stored.State);
        Assert.Null(stored.ItemId);
        Assert.Null(await store.FindOwnerAsync("aaaaaaaaaaa"));
    }

    [Fact]
    public void JobsForVideosInTheLibraryAreNeverPruned()
    {
        var jobs = Enumerable.Range(0, JobStore.MaxJobs + 50)
            .Select(i => new JobRecord { Id = Guid.NewGuid(), State = JobState.InLibrary, UpdatedUtc = Now.AddDays(-400 - i) })
            .ToList();

        JobStore.Prune(jobs, Now);

        Assert.Equal(JobStore.MaxJobs + 50, jobs.Count);
    }
}
