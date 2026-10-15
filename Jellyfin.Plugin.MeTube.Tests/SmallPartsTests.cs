using System;
using System.IO;
using System.Security.Claims;
using Jellyfin.Plugin.MeTube.Api;
using Jellyfin.Plugin.MeTube.Jobs;
using Jellyfin.Plugin.MeTube.Library;
using Jellyfin.Plugin.MeTube.Search;
using Jellyfin.Plugin.MeTube.Web;
using Xunit;

namespace Jellyfin.Plugin.MeTube.Tests;

public class PathMapperTests
{
    [Fact]
    public void JoinsTheLibraryFolderAndMeTubesFileName()
    {
        Assert.True(PathMapper.TryMap("/media/youtube", "jawed/Me at the zoo [jNQXAC9IVRw].mp4", out var path));
        Assert.Equal(Path.Combine("/media/youtube", "jawed", "Me at the zoo [jNQXAC9IVRw].mp4"), path);
    }

    [Theory]
    [InlineData("../movies/x.mkv")]
    [InlineData("jawed/../../etc/passwd")]
    [InlineData("./x.mkv")]
    [InlineData("/etc/passwd")]
    [InlineData("\\server\\share\\x.mkv")]
    [InlineData("C:\\x.mkv")]
    [InlineData("jawed//x.mkv")]
    [InlineData("")]
    [InlineData(null)]
    public void RefusesAnythingThatCouldLeaveTheFolder(string? file)
    {
        Assert.False(PathMapper.TryMap("/media/youtube", file, out _));
    }

    [Fact]
    public void NeedsALibraryFolder()
    {
        Assert.False(PathMapper.TryMap(null, "x [jNQXAC9IVRw].mp4", out _));
    }
}

public class LibraryIndexTests
{
    [Theory]
    [InlineData("Me at the zoo [jNQXAC9IVRw].mp4", "jNQXAC9IVRw")]
    [InlineData("A [b] title [jNQXAC9IVRw].mkv", "jNQXAC9IVRw")]
    [InlineData("x [a-b_c12345Z].webm", "a-b_c12345Z")]
    public void ReadsTheVideoIdFromTheFileName(string name, string id)
    {
        Assert.Equal(id, LibraryIndex.VideoIdFromFileName(name));
    }

    [Theory]
    [InlineData("Me at the zoo [jNQXAC9IVRw].jpg")]
    [InlineData("Me at the zoo [jNQXAC9IVRw].nl.vtt")]
    [InlineData("Me at the zoo.mp4")]
    [InlineData("Me at the zoo [jNQXAC9IVR].mp4")]
    public void IgnoresSidecarsAndOtherFiles(string name)
    {
        Assert.Null(LibraryIndex.VideoIdFromFileName(name));
    }
}

public class UserAccessTests
{
    [Fact]
    public void AdminsAreAlwaysAllowed()
    {
        Assert.True(UserAccess.IsAllowed(Guid.NewGuid(), true, Array.Empty<Guid>()));
    }

    [Fact]
    public void OthersOnlyWhenListed()
    {
        var user = Guid.NewGuid();
        Assert.False(UserAccess.IsAllowed(user, false, Array.Empty<Guid>()));
        Assert.False(UserAccess.IsAllowed(user, false, null));
        Assert.True(UserAccess.IsAllowed(user, false, new[] { Guid.NewGuid(), user }));
    }

    [Fact]
    public void ApiKeysWithoutAUserAreNeverAllowed()
    {
        // API keys authenticate as administrators with no user id.
        Assert.False(UserAccess.IsAllowed(Guid.Empty, true, Array.Empty<Guid>()));
    }

    [Fact]
    public void ReadsTheJellyfinUserIdClaim()
    {
        var id = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("Jellyfin-UserId", id.ToString("N")),
            new Claim(ClaimTypes.Role, "Administrator")
        }, "CustomAuthentication"));

        Assert.Equal(id, UserAccess.GetUserId(principal));
        Assert.True(UserAccess.IsAdmin(principal));
        Assert.Equal(Guid.Empty, UserAccess.GetUserId(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}

public class SearchServiceTests
{
    [Theory]
    [InlineData("  me   at\tthe zoo ", "me at the zoo")]
    [InlineData("a\u0000b", "a b")]
    [InlineData("me\nat the zoo", "me at the zoo")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void NormalizesQueries(string? input, string? expected)
    {
        Assert.Equal(expected, SearchService.NormalizeQuery(input));
    }

    [Theory]
    [InlineData("EpUDEgxtZSBhdCB0aGUgem9v-_%3D", true)]
    [InlineData("abc def", false)]
    [InlineData("abc\"def", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ValidatesContinuationTokens(string? token, bool valid)
    {
        Assert.Equal(valid, SearchService.IsValidContinuation(token));
    }

    [Fact]
    public void RejectsHugeContinuationTokens()
    {
        Assert.False(SearchService.IsValidContinuation(new string('a', 5000)));
    }
}

public class WebInjectionTests
{
    [Fact]
    public void InsertsTheScriptBeforeTheLastBodyTag()
    {
        var html = "<html><body><div></div></body></html>";
        var patched = WebInjectionStartupFilter.Patch(html, "/jf");

        Assert.Equal("<html><body><div></div><script src=\"/jf/MeTube/ClientScript\" defer></script>\n</body></html>", patched);
    }

    [Fact]
    public void IsIdempotent()
    {
        var once = WebInjectionStartupFilter.Patch("<body></body>", string.Empty);
        Assert.Equal(once, WebInjectionStartupFilter.Patch(once, string.Empty));
    }

    [Fact]
    public void AppendsWhenThereIsNoBodyTag()
    {
        Assert.EndsWith("<script src=\"/MeTube/ClientScript\" defer></script>\n", WebInjectionStartupFilter.Patch("<p>x</p>", string.Empty));
    }

    [Fact]
    public void KeepsOtherPluginsScripts()
    {
        var html = "<body><script src=\"/GuestPass/ClientScript\" defer></script></body>";
        var patched = WebInjectionStartupFilter.Patch(html, string.Empty);

        Assert.Contains("/GuestPass/ClientScript", patched);
        Assert.Contains("/MeTube/ClientScript", patched);
    }
}

public class YouTubeGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PausesForFiveMinutes()
    {
        var guard = new YouTubeGuard();
        Assert.False(guard.IsPaused(Now));

        guard.Pause(Now);

        Assert.True(guard.IsPaused(Now.AddMinutes(4)));
        Assert.False(guard.IsPaused(Now.AddMinutes(6)));
    }

    [Fact]
    public void LimitsPerUserAndPerBucket()
    {
        var guard = new YouTubeGuard();
        var alice = Guid.NewGuid();

        Assert.True(guard.TryCount(alice, "add", 2, Now));
        Assert.True(guard.TryCount(alice, "add", 2, Now));
        Assert.False(guard.TryCount(alice, "add", 2, Now));
        Assert.True(guard.TryCount(alice, "search", 2, Now));
        Assert.True(guard.TryCount(Guid.NewGuid(), "add", 2, Now));
        Assert.True(guard.TryCount(alice, "add", 2, Now.AddSeconds(61)));
    }
}

public class JobServiceOptionsTests
{
    [Fact]
    public void DefaultsMatchTheSetupGuide()
    {
        var options = JobService.CurrentOptions();

        Assert.Equal("youtube", options.Folder);
        Assert.Equal(new[] { "jellyfin" }, options.Presets);
        Assert.Equal("best", options.Quality);
        Assert.Equal("ios", options.Format);
        Assert.Equal("auto", options.Codec);
    }
}
