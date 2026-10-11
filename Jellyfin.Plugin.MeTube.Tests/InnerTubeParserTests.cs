using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.MeTube.Search;
using Xunit;

namespace Jellyfin.Plugin.MeTube.Tests;

public class InnerTubeParserTests
{
    [Fact]
    public void ParsesTheFirstPageOfARealSearch()
    {
        using var doc = FixtureFile.Json("search_me_at_the_zoo.json");
        var page = InnerTubeParser.ParseSearch(doc.RootElement);

        Assert.Equal(20, page.Items.Count);
        var first = page.Items[0];
        Assert.Equal("jNQXAC9IVRw", first.VideoId);
        Assert.Equal("Me at the zoo", first.Title);
        Assert.Equal("jawed", first.Channel);
        Assert.Equal("UC4QobU6STFB0P71PMvOGN5A", first.ChannelId);
        Assert.Equal(19, first.DurationSeconds);
        Assert.Equal("0:19", first.DurationText);
        Assert.False(first.IsLive);
        Assert.False(first.IsUpcoming);
        Assert.False(string.IsNullOrEmpty(first.PublishedText));
        Assert.Contains("views", first.ViewsText);
        Assert.Equal("https://i.ytimg.com/vi/jNQXAC9IVRw/mqdefault.jpg", first.ThumbnailUrl);
        Assert.All(page.Items, v => Assert.True(VideoIdParser.IsValidId(v.VideoId)));
    }

    [Fact]
    public void TakesTheContinuationFromTheResultsNotFromTheFilterChips()
    {
        using var doc = FixtureFile.Json("search_me_at_the_zoo.json");
        var page = InnerTubeParser.ParseSearch(doc.RootElement);

        var all = new List<string>();
        CollectTokens(doc.RootElement, all);
        var expected = doc.RootElement
            .GetProperty("contents").GetProperty("twoColumnSearchResultsRenderer").GetProperty("primaryContents")
            .GetProperty("sectionListRenderer").GetProperty("contents").EnumerateArray()
            .Where(s => s.TryGetProperty("continuationItemRenderer", out _))
            .Select(s => s.GetProperty("continuationItemRenderer").GetProperty("continuationEndpoint")
                .GetProperty("continuationCommand").GetProperty("token").GetString())
            .Single();

        // The response carries several continuation commands (header filter chips);
        // the test is only meaningful if picking the wrong one were possible.
        Assert.True(all.Count > 1);
        Assert.Equal(expected, page.Continuation);
    }

    [Fact]
    public void ParsesAContinuationPage()
    {
        using var doc = FixtureFile.Json("search_continuation.json");
        var page = InnerTubeParser.ParseSearch(doc.RootElement);

        Assert.True(page.Items.Count >= 10);
        Assert.All(page.Items, v => Assert.True(VideoIdParser.IsValidId(v.VideoId)));
        Assert.False(string.IsNullOrEmpty(page.Continuation));
    }

    [Fact]
    public void FlagsLiveStreamsWhichHaveNoLength()
    {
        using var doc = FixtureFile.Json("search_live.json");
        var page = InnerTubeParser.ParseSearch(doc.RootElement);

        var live = page.Items.Single(v => v.VideoId == "rFZHOHl-L8A");
        Assert.True(live.IsLive);
        Assert.Null(live.DurationSeconds);
        Assert.Null(live.DurationText);

        var recording = page.Items.Single(v => v.VideoId == "i43tkaTXtwI");
        Assert.False(recording.IsLive);
        Assert.Equal((3 * 3600) + (11 * 60) + 14, recording.DurationSeconds);
    }

    [Fact]
    public void ReadsLengthFromThePlayerEvenWhenUnplayable()
    {
        using var doc = FixtureFile.Json("player_jNQXAC9IVRw.json");
        var info = InnerTubeParser.ParsePlayer(doc.RootElement, "jNQXAC9IVRw");

        Assert.True(info.Available);
        Assert.True(info.FromPlayer);
        Assert.Equal(19, info.DurationSeconds);
        Assert.False(info.IsLive);
        Assert.Equal("Me at the zoo", info.Title);
        Assert.Equal("jawed", info.Channel);
    }

    [Fact]
    public void PlayerReportsLiveStreams()
    {
        using var doc = FixtureFile.Json("player_live.json");
        var info = InnerTubeParser.ParsePlayer(doc.RootElement, "rFZHOHl-L8A");

        Assert.True(info.Available);
        Assert.True(info.IsLive);
        Assert.Null(info.DurationSeconds);
    }

    [Fact]
    public void PlayerReportsUnavailableVideos()
    {
        using var doc = FixtureFile.Json("player_unavailable.json");
        var info = InnerTubeParser.ParsePlayer(doc.RootElement, "aaaaaaaaaaa");

        Assert.False(info.Available);
        Assert.False(string.IsNullOrWhiteSpace(info.UnavailableReason));
    }

    [Fact]
    public void PlayerRejectsAnAnswerForAnotherVideo()
    {
        using var doc = FixtureFile.Json("player_jNQXAC9IVRw.json");
        var info = InnerTubeParser.ParsePlayer(doc.RootElement, "aaaaaaaaaaa");

        Assert.False(info.Available);
    }

    [Fact]
    public void TreatsAJustEndedStreamAsLive()
    {
        using var doc = JsonDocument.Parse("""{"videoDetails":{"videoId":"aaaaaaaaaaa","title":"Ended stream","lengthSeconds":"0","isPostLiveDvr":true}}""");
        var info = InnerTubeParser.ParsePlayer(doc.RootElement, "aaaaaaaaaaa");

        Assert.True(info.Available);
        Assert.True(info.IsLive);
        Assert.Null(info.DurationSeconds);
    }

    [Fact]
    public void ParsesUpcomingVideos()
    {
        using var doc = JsonDocument.Parse("""{"videoId":"aaaaaaaaaaa","title":{"runs":[{"text":"Premiere"}]},"upcomingEventData":{"startTime":"1800000000"}}""");
        var video = InnerTubeParser.ParseVideoRenderer(doc.RootElement);

        Assert.NotNull(video);
        Assert.True(video!.IsUpcoming);
        Assert.False(video.IsLive);
    }

    [Theory]
    [InlineData("0:19", 19)]
    [InlineData("12:34", 754)]
    [InlineData("1:02:03", 3723)]
    [InlineData("10:00:00", 36000)]
    public void ParsesDurations(string text, int seconds)
    {
        Assert.Equal(seconds, InnerTubeParser.ParseDuration(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("LIVE")]
    [InlineData("1:xx")]
    public void RejectsBadDurations(string? text)
    {
        Assert.Null(InnerTubeParser.ParseDuration(text));
    }

    [Theory]
    [InlineData(19, "0:19")]
    [InlineData(754, "12:34")]
    [InlineData(3723, "1:02:03")]
    public void FormatsDurations(int seconds, string text)
    {
        Assert.Equal(text, InnerTubeParser.FormatDuration(seconds));
    }

    private static void CollectTokens(JsonElement element, List<string> tokens)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name == "continuationCommand" && property.Value.TryGetProperty("token", out var token))
                {
                    tokens.Add(token.GetString()!);
                }

                CollectTokens(property.Value, tokens);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectTokens(item, tokens);
            }
        }
    }
}
