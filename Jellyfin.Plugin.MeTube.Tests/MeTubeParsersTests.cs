using System.Text.Json;
using Jellyfin.Plugin.MeTube.MeTube;
using Xunit;

namespace Jellyfin.Plugin.MeTube.Tests;

public class MeTubeParsersTests
{
    private const string History = """
        {
          "done": [
            {"id":"jNQXAC9IVRw","title":"Me at the zoo","url":"https://www.youtube.com/watch?v=jNQXAC9IVRw","status":"finished",
             "folder":"youtube","filename":"jawed/Me at the zoo [jNQXAC9IVRw].mp4","timestamp":1791652762449649105,"percent":100.0,"msg":null,"error":null},
            {"id":"bbbbbbbbbbb","title":"Broken","url":"https://www.youtube.com/watch?v=bbbbbbbbbbb","status":"error",
             "folder":"youtube","filename":null,"timestamp":1791652762000000000,"msg":"ERROR: Video unavailable","error":"DownloadError"}
          ],
          "queue": [
            {"id":"aaaaaaaaaaa","title":"Running","url":"https://www.youtube.com/watch?v=aaaaaaaaaaa","status":"downloading",
             "folder":"youtube","timestamp":1791652800000000000,"percent":42.5}
          ],
          "pending": []
        }
        """;

    [Fact]
    public void ParsesHistory()
    {
        using var doc = JsonDocument.Parse(History);
        var history = MeTubeParsers.ParseHistory(doc.RootElement);

        Assert.Equal(2, history.Done.Count);
        Assert.Single(history.Queue);
        Assert.Empty(history.Pending);

        var done = history.Done[0];
        Assert.Equal("jNQXAC9IVRw", done.Id);
        Assert.Equal("finished", done.Status);
        Assert.Equal("youtube", done.Folder);
        Assert.Equal("jawed/Me at the zoo [jNQXAC9IVRw].mp4", done.Filename);
        Assert.Equal(1791652762449649105L, done.Timestamp);
        Assert.Equal(42.5, history.Queue[0].Percent);
        Assert.Equal("ERROR: Video unavailable", history.Done[1].Msg);
    }

    [Fact]
    public void ParsesTheOlderPairFormat()
    {
        using var doc = JsonDocument.Parse("""{"done":[["https://www.youtube.com/watch?v=jNQXAC9IVRw",{"id":"jNQXAC9IVRw","status":"finished"}]],"queue":[],"pending":[]}""");
        var history = MeTubeParsers.ParseHistory(doc.RootElement);

        Assert.Equal("jNQXAC9IVRw", Assert.Single(history.Done).Id);
    }

    [Fact]
    public void TreatsHttp400AsAnErrorWithItsText()
    {
        var result = MeTubeParsers.ParseAddResponse(400, "Unknown preset: jellyfin");
        Assert.Equal(MeTubeAddOutcome.Error, result.Outcome);
        Assert.Equal("Unknown preset: jellyfin", result.Message);
        Assert.True(result.ConfigProblem);
    }

    [Fact]
    public void AYtDlpRefusalIsNotAConfigProblem()
    {
        var result = MeTubeParsers.ParseAddResponse(200, """{"status":"error","msg":"ERROR: Join this channel to get access to members-only content"}""");
        Assert.Equal(MeTubeAddOutcome.Error, result.Outcome);
        Assert.False(result.ConfigProblem);
    }

    [Fact]
    public void TreatsStatusOkAsQueued()
    {
        Assert.Equal(MeTubeAddOutcome.Ok, MeTubeParsers.ParseAddResponse(200, """{"status":"ok"}""").Outcome);
    }

    [Fact]
    public void RecognisesAnAlreadyQueuedUrl()
    {
        var result = MeTubeParsers.ParseAddResponse(200, """{"status":"ok","msg":"Already in queue: https://www.youtube.com/watch?v=jNQXAC9IVRw"}""");
        Assert.Equal(MeTubeAddOutcome.AlreadyQueued, result.Outcome);
    }

    [Fact]
    public void TreatsStatusErrorAsAnError()
    {
        var result = MeTubeParsers.ParseAddResponse(200, """{"status":"error","msg":"Folder is not allowed"}""");
        Assert.Equal(MeTubeAddOutcome.Error, result.Outcome);
        Assert.Equal("Folder is not allowed", result.Message);
    }

    [Theory]
    [InlineData(500, "oops")]
    [InlineData(200, "not json")]
    [InlineData(200, "")]
    public void TreatsAnythingUnexpectedAsAnError(int status, string body)
    {
        Assert.Equal(MeTubeAddOutcome.Error, MeTubeParsers.ParseAddResponse(status, body).Outcome);
    }

    [Theory]
    [InlineData("""{"presets":[]}""", "")]
    [InlineData("""{"presets":["jellyfin","subtitles"]}""", "jellyfin,subtitles")]
    [InlineData("""{"presets":[{"name":"jellyfin"}]}""", "jellyfin")]
    [InlineData("""{"presets":{"jellyfin":{},"subtitles":{}}}""", "jellyfin,subtitles")]
    public void ParsesPresetLists(string json, string expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, string.Join(',', MeTubeParsers.ParsePresets(doc.RootElement)));
    }

    [Fact]
    public void ParsesTheRealBrowseSearch()
    {
        using var doc = FixtureFile.Json("metube_browse.json");
        var page = MeTubeParsers.ParseBrowse(doc.RootElement);

        Assert.Equal(5, page.Items.Count);
        Assert.Equal("jNQXAC9IVRw", page.Items[0].VideoId);
        Assert.Equal("Me at the zoo", page.Items[0].Title);
        Assert.Equal(19, page.Items[0].DurationSeconds);
        Assert.Equal("0:19", page.Items[0].DurationText);
        Assert.False(page.Items[0].IsLive);
        Assert.Null(page.Continuation);
    }

    [Fact]
    public void BrowseErrorsGiveNoResults()
    {
        using var doc = JsonDocument.Parse("""{"status":"error","msg":"nope"}""");
        Assert.Empty(MeTubeParsers.ParseBrowse(doc.RootElement).Items);
    }

    [Theory]
    [InlineData("http://192.168.1.10:8081", "add", "http://192.168.1.10:8081/add")]
    [InlineData("http://192.168.1.10:8081/", "history", "http://192.168.1.10:8081/history")]
    [InlineData("https://nas.lan/metube", "add", "https://nas.lan/metube/add")]
    public void BuildsMeTubeUrls(string baseUrl, string path, string expected)
    {
        Assert.Equal(expected, MeTubeClient.BuildUri(baseUrl, path).ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("192.168.1.10:8081")]
    [InlineData("file:///etc/passwd")]
    public void RefusesBadMeTubeUrls(string baseUrl)
    {
        Assert.Throws<MeTubeException>(() => MeTubeClient.BuildUri(baseUrl, "add"));
    }
}
