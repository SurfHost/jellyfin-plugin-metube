using System;
using Jellyfin.Plugin.MeTube.Jobs;
using Jellyfin.Plugin.MeTube.MeTube;
using Jellyfin.Plugin.MeTube.Models;
using Xunit;

namespace Jellyfin.Plugin.MeTube.Tests;

public class JobTrackerTests
{
    private const string VideoId = "jNQXAC9IVRw";
    private static readonly DateTimeOffset Created = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static JobRecord NewJob() => new()
    {
        Id = Guid.NewGuid(),
        VideoId = VideoId,
        State = JobState.Queued,
        CreatedUtc = Created,
        UpdatedUtc = Created,
        MatchSinceNs = JobTracker.ToUnixNanoseconds(Created - JobTracker.MatchSlack)
    };

    private static MeTubeEntry Entry(string status, DateTimeOffset createdAt, string folder = "youtube", string? filename = null) => new()
    {
        Id = VideoId,
        Url = "https://www.youtube.com/watch?v=" + VideoId,
        Status = status,
        Folder = folder,
        Filename = filename,
        Timestamp = JobTracker.ToUnixNanoseconds(createdAt),
        Percent = status == "downloading" ? 42 : null
    };

    [Fact]
    public void ConvertsToUnixNanoseconds()
    {
        Assert.Equal(1_000_000_000L, JobTracker.ToUnixNanoseconds(DateTimeOffset.UnixEpoch.AddSeconds(1)));
    }

    [Fact]
    public void IgnoresAStaleFinishedEntryFromAnEarlierDownload()
    {
        // The scenario from the design review: MeTube keeps the old "finished" entry
        // for the URL until the new download completes.
        var history = new MeTubeHistory();
        history.Done.Add(Entry("finished", Created.AddDays(-3), filename: "old.mp4"));

        var result = JobTracker.Evaluate(NewJob(), history, "youtube");

        Assert.False(result.Seen);
    }

    [Fact]
    public void PrefersTheActiveEntryOverAFinishedOne()
    {
        var history = new MeTubeHistory();
        history.Done.Add(Entry("finished", Created.AddSeconds(5), filename: "x.mp4"));
        history.Queue.Add(Entry("downloading", Created.AddSeconds(1)));

        var result = JobTracker.Evaluate(NewJob(), history, "youtube");

        Assert.True(result.Seen);
        Assert.Equal(JobState.Downloading, result.State);
        Assert.Equal(42, result.Percent);
    }

    [Fact]
    public void ReportsFinishedWithTheFileName()
    {
        var history = new MeTubeHistory();
        history.Done.Add(Entry("finished", Created.AddSeconds(2), filename: "jawed/Me at the zoo [jNQXAC9IVRw].mp4"));

        var result = JobTracker.Evaluate(NewJob(), history, "youtube");

        Assert.Equal(JobState.Finished, result.State);
        Assert.Equal("jawed/Me at the zoo [jNQXAC9IVRw].mp4", result.Filename);
    }

    [Fact]
    public void ReportsErrorsWithMeTubesMessage()
    {
        var history = new MeTubeHistory();
        var entry = Entry("error", Created.AddSeconds(2));
        entry.Msg = "ERROR: Sign in to confirm your age";
        history.Done.Add(entry);

        var result = JobTracker.Evaluate(NewJob(), history, "youtube");

        Assert.Equal(JobState.Failed, result.State);
        Assert.Equal("ERROR: Sign in to confirm your age", result.Error);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("queued")]
    [InlineData("preparing")]
    [InlineData("scheduled")]
    public void TreatsWaitingStatesAsQueued(string status)
    {
        var history = new MeTubeHistory();
        history.Pending.Add(Entry(status, Created));

        Assert.Equal(JobState.Queued, JobTracker.Evaluate(NewJob(), history, "youtube").State);
    }

    [Fact]
    public void IgnoresTheSameVideoInAnotherFolder()
    {
        var history = new MeTubeHistory();
        history.Queue.Add(Entry("downloading", Created, folder: ""));

        Assert.False(JobTracker.Evaluate(NewJob(), history, "youtube").Seen);
    }

    [Fact]
    public void FolderComparisonIgnoresSlashesAndCase()
    {
        var history = new MeTubeHistory();
        history.Queue.Add(Entry("downloading", Created, folder: "YouTube/"));

        Assert.True(JobTracker.Evaluate(NewJob(), history, "/youtube").Seen);
    }

    [Fact]
    public void MatchesByUrlWhenTheIdIsMissing()
    {
        var history = new MeTubeHistory();
        var entry = Entry("downloading", Created);
        entry.Id = null;
        entry.Url = "https://youtu.be/" + VideoId;
        history.Queue.Add(entry);

        Assert.True(JobTracker.Evaluate(NewJob(), history, "youtube").Seen);
    }

    [Fact]
    public void AnAlreadyQueuedJobMatchesAnOlderQueueEntry()
    {
        var job = NewJob();
        job.MatchSinceNs = 0;
        var history = new MeTubeHistory();
        history.Queue.Add(Entry("downloading", Created.AddHours(-1)));

        Assert.True(JobTracker.Evaluate(job, history, "youtube").Seen);
    }

    [Fact]
    public void AQueueEntryThatSaysFinishedIsOnlyAFinishedPart()
    {
        // yt-dlp reports "finished" after the video part of a merged download, before
        // the audio part starts; only an entry in "done" is really finished.
        var history = new MeTubeHistory();
        history.Queue.Add(Entry("finished", Created.AddSeconds(1), filename: ".metube-tmp/x [jNQXAC9IVRw].f399.mp4"));

        var result = JobTracker.Evaluate(NewJob(), history, "youtube");

        Assert.Equal(JobState.Downloading, result.State);
        Assert.Null(result.Filename);
    }

    [Fact]
    public void AnyOtherFinalStatusInDoneIsAFailure()
    {
        var history = new MeTubeHistory();
        history.Done.Add(Entry("canceled", Created.AddSeconds(1)));

        Assert.Equal(JobState.Failed, JobTracker.Evaluate(NewJob(), history, "youtube").State);
    }

    [Fact]
    public void ReportsWhereTheEntryCameFrom()
    {
        var history = new MeTubeHistory();
        history.Done.Add(Entry("finished", Created.AddSeconds(1), filename: "x.mp4"));
        var (entry, fromDone) = JobTracker.FindEntry(NewJob(), history, "youtube");

        Assert.NotNull(entry);
        Assert.True(fromDone);
    }

    [Fact]
    public void MissingNeedsBothThreePollsAndThirtySeconds()
    {
        // Polls also run after every Add, so three can happen within seconds.
        Assert.False(JobTracker.IsMissing(3, Created, Created.AddSeconds(5)));
        Assert.False(JobTracker.IsMissing(2, Created, Created.AddMinutes(5)));
        Assert.True(JobTracker.IsMissing(3, Created, Created.AddSeconds(31)));
    }

    [Fact]
    public void GivesUpAfterTheDeadline()
    {
        var job = NewJob();
        Assert.False(JobTracker.IsPastDeadline(job, Created.AddHours(11)));
        Assert.True(JobTracker.IsPastDeadline(job, Created.AddHours(13)));

        job.State = JobState.Finished;
        Assert.False(JobTracker.IsPastDeadline(job, Created.AddHours(13)));
    }

    [Fact]
    public void StopsWaitingForTheLibraryAfterTenMinutes()
    {
        var job = NewJob();
        job.State = JobState.Finished;
        job.FinishedUtc = Created;

        Assert.False(JobTracker.IsLibraryWaitOver(job, Created.AddMinutes(9)));
        Assert.True(JobTracker.IsLibraryWaitOver(job, Created.AddMinutes(11)));
    }
}
