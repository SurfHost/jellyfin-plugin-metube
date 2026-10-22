using System;
using System.IO;
using Jellyfin.Plugin.MeTube.Jobs;
using Jellyfin.Plugin.MeTube.MeTube;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MeTube.Tests;

public class DownloadFormatTests
{
    private static readonly MeTubeAddOptions Server = new("youtube", new[] { "jellyfin" }, "1080", "mp4", "h264");

    [Fact]
    public void CompatibleIsH264AndAacInMp4()
    {
        var options = DownloadFormats.Apply(Server, "compatible");
        Assert.Equal(("ios", "best", "auto"), (options.Format, options.Quality, options.Codec));
        Assert.Equal("youtube", options.Folder);
    }

    [Fact]
    public void BestIsTheBestYouTubeHas()
    {
        var options = DownloadFormats.Apply(Server, "best");
        Assert.Equal(("any", "best", "auto"), (options.Format, options.Quality, options.Codec));
    }

    [Theory]
    [InlineData("default")]
    [InlineData(null)]
    [InlineData("4k-please")]
    public void AnythingElseKeepsTheServerSettings(string? choice)
    {
        Assert.Equal(Server, DownloadFormats.Apply(Server, choice));
    }

    [Theory]
    [InlineData("best", "compatible", "best")]
    [InlineData("Compatible", "best", "compatible")]
    [InlineData("default", "best", "default")]
    [InlineData(null, "best", "best")]
    [InlineData("", "compatible", "compatible")]
    [InlineData(" ", null, "default")]
    public void TheFormatSentWithAnAddWinsOverTheStoredOne(string? requested, string? stored, string expected)
    {
        Assert.Equal(expected, DownloadFormats.Resolve(requested, stored));
    }

    [Theory]
    [InlineData("default", true)]
    [InlineData("Compatible", true)]
    [InlineData(" best ", true)]
    [InlineData("ios", false)]
    [InlineData(null, false)]
    public void OnlyKnownChoicesAreAccepted(string? choice, bool known)
    {
        Assert.Equal(known, DownloadFormats.IsKnown(choice));
    }
}

public sealed class UserPreferenceStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtj-prefs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void RemembersEachUsersChoiceAcrossRestarts()
    {
        var hans = Guid.NewGuid();
        var merle = Guid.NewGuid();
        var store = NewStore();
        store.SetFormat(hans, "best");
        store.SetFormat(merle, "compatible");

        var reloaded = NewStore();
        Assert.Equal("best", reloaded.GetFormat(hans));
        Assert.Equal("compatible", reloaded.GetFormat(merle));
        Assert.Equal("default", reloaded.GetFormat(Guid.NewGuid()));
    }

    [Fact]
    public void DefaultForgetsTheChoice()
    {
        var user = Guid.NewGuid();
        var store = NewStore();
        store.SetFormat(user, "best");
        store.SetFormat(user, "default");

        Assert.Equal("default", NewStore().GetFormat(user));
    }

    private UserPreferenceStore NewStore() => new(_dir, NullLogger<UserPreferenceStore>.Instance);
}
