using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.MeTube.Jobs;
using Jellyfin.Plugin.MeTube.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MeTube.Tests;

public sealed class JobStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "metube-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private JobStore NewStore() => new(_dir, NullLogger<JobStore>.Instance);

    private static JobRecord Candidate(string videoId, Guid user) => new() { VideoId = videoId, UserId = user, Title = videoId, State = JobState.Queued };

    [Fact]
    public async Task CreatesAJobAndPersistsIt()
    {
        var (outcome, job) = await NewStore().ReserveAsync(Candidate("aaaaaaaaaaa", Alice), 25, false, Now);

        Assert.Equal(ReserveOutcome.Created, outcome);
        Assert.NotEqual(Guid.Empty, job!.Id);

        var reloaded = await NewStore().ListAsync();
        Assert.Equal(job.Id, Assert.Single(reloaded).Id);
    }

    [Fact]
    public async Task ReturnsTheOpenJobForASecondAddOfTheSameVideo()
    {
        var store = NewStore();
        var (_, first) = await store.ReserveAsync(Candidate("aaaaaaaaaaa", Alice), 25, false, Now);
        var (outcome, second) = await store.ReserveAsync(Candidate("aaaaaaaaaaa", Bob), 25, false, Now);

        Assert.Equal(ReserveOutcome.Existing, outcome);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task RecordsOtherUsersWhoAskForAnOpenJob()
    {
        var store = NewStore();
        var (_, job) = await store.ReserveAsync(Candidate("aaaaaaaaaaa", Alice), 25, false, Now);

        var attached = await store.AttachToOpenAsync("aaaaaaaaaaa", Bob, Now);
        await store.AttachToOpenAsync("aaaaaaaaaaa", Bob, Now);
        await store.AttachToOpenAsync("aaaaaaaaaaa", Alice, Now);

        Assert.Equal(job!.Id, attached!.Id);
        Assert.Equal(new[] { Bob }, Assert.Single(await NewStore().ListAsync()).RequestedBy);
        Assert.Null(await store.AttachToOpenAsync("bbbbbbbbbbb", Bob, Now));
    }

    [Fact]
    public async Task ClonesDoNotShareTheRequesterList()
    {
        var store = NewStore();
        await store.ReserveAsync(Candidate("aaaaaaaaaaa", Alice), 25, false, Now);
        var copy = await store.AttachToOpenAsync("aaaaaaaaaaa", Bob, Now);
        copy!.RequestedBy.Add(Guid.NewGuid());

        Assert.Single(Assert.Single(await store.ListAsync()).RequestedBy);
    }

    [Fact]
    public async Task AllowsANewJobOnceTheOldOneIsClosed()
    {
        var store = NewStore();
        var (_, first) = await store.ReserveAsync(Candidate("aaaaaaaaaaa", Alice), 25, false, Now);
        await store.UpdateAsync(first!.Id, j => j.State = JobState.Failed, Now);

        var (outcome, _) = await store.ReserveAsync(Candidate("aaaaaaaaaaa", Alice), 25, false, Now);
        Assert.Equal(ReserveOutcome.Created, outcome);
    }

    [Fact]
    public async Task EnforcesTheDailyLimitPerUser()
    {
        var store = NewStore();
        await store.ReserveAsync(Candidate("aaaaaaaaaaa", Alice), 2, false, Now);
        await store.ReserveAsync(Candidate("bbbbbbbbbbb", Alice), 2, false, Now);

        var (limited, _) = await store.ReserveAsync(Candidate("ccccccccccc", Alice), 2, false, Now);
        var (otherUser, _) = await store.ReserveAsync(Candidate("ccccccccccc", Bob), 2, false, Now);
        var (exempt, _) = await store.ReserveAsync(Candidate("ddddddddddd", Alice), 2, true, Now);
        var (nextDay, _) = await store.ReserveAsync(Candidate("eeeeeeeeeee", Alice), 2, false, Now.AddHours(25));

        Assert.Equal(ReserveOutcome.LimitReached, limited);
        Assert.Equal(ReserveOutcome.Created, otherUser);
        Assert.Equal(ReserveOutcome.Created, exempt);
        Assert.Equal(ReserveOutcome.Created, nextDay);
    }

    [Fact]
    public async Task ARemovedJobDoesNotCountAgainstTheLimit()
    {
        var store = NewStore();
        var (_, job) = await store.ReserveAsync(Candidate("aaaaaaaaaaa", Alice), 1, false, Now);
        await store.RemoveAsync(job!.Id, Now);

        var (outcome, _) = await store.ReserveAsync(Candidate("bbbbbbbbbbb", Alice), 1, false, Now);
        Assert.Equal(ReserveOutcome.Created, outcome);
    }

    [Fact]
    public async Task ConcurrentAddsOfOneVideoCreateOneJob()
    {
        var store = NewStore();
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => store.ReserveAsync(Candidate("aaaaaaaaaaa", Alice), 25, false, Now)));

        Assert.Single(results, r => r.Outcome == ReserveOutcome.Created);
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task KeepsProgressInMemoryAndDropsItWhenClosed()
    {
        var store = NewStore();
        var (_, job) = await store.ReserveAsync(Candidate("aaaaaaaaaaa", Alice), 25, false, Now);
        store.SetProgress(job!.Id, 150);
        Assert.Equal(100, store.GetProgress(job.Id));

        await store.UpdateAsync(job.Id, j => j.State = JobState.InLibrary, Now);
        Assert.Null(store.GetProgress(job.Id));
    }

    [Fact]
    public void PrunesOldClosedJobsAndCapsTheCount()
    {
        var jobs = new List<JobRecord>
        {
            new() { Id = Guid.NewGuid(), State = JobState.InLibrary, UpdatedUtc = Now.AddDays(-31) },
            new() { Id = Guid.NewGuid(), State = JobState.Queued, UpdatedUtc = Now.AddDays(-31) },
            new() { Id = Guid.NewGuid(), State = JobState.Failed, UpdatedUtc = Now.AddDays(-1) }
        };

        JobStore.Prune(jobs, Now);

        Assert.Equal(2, jobs.Count);
        Assert.DoesNotContain(jobs, j => j.State == JobState.InLibrary);

        var many = Enumerable.Range(0, JobStore.MaxJobs + 20)
            .Select(i => new JobRecord { Id = Guid.NewGuid(), State = JobState.InLibrary, UpdatedUtc = Now.AddMinutes(-i) })
            .ToList();
        JobStore.Prune(many, Now);
        Assert.Equal(JobStore.MaxJobs, many.Count);
    }

    [Fact]
    public async Task StartsEmptyWhenTheFileIsCorrupt()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "jobs.json"), "{ not json");

        Assert.Empty(await NewStore().ListAsync());
        Assert.True(File.Exists(Path.Combine(_dir, "jobs.json.corrupt")));
    }
}
