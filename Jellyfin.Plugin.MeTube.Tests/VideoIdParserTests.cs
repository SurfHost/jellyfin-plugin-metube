using Jellyfin.Plugin.MeTube.Search;
using Xunit;

namespace Jellyfin.Plugin.MeTube.Tests;

public class VideoIdParserTests
{
    [Theory]
    [InlineData("jNQXAC9IVRw")]
    [InlineData("  jNQXAC9IVRw  ")]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw")]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw&t=42s")]
    [InlineData("https://www.youtube.com/watch?feature=share&v=jNQXAC9IVRw&list=PL123")]
    [InlineData("http://youtube.com/watch?v=jNQXAC9IVRw")]
    [InlineData("youtube.com/watch?v=jNQXAC9IVRw")]
    [InlineData("https://m.youtube.com/watch?v=jNQXAC9IVRw")]
    [InlineData("https://music.youtube.com/watch?v=jNQXAC9IVRw")]
    [InlineData("https://youtu.be/jNQXAC9IVRw")]
    [InlineData("https://youtu.be/jNQXAC9IVRw?si=AbCdEf123&t=10")]
    [InlineData("https://www.youtube.com/shorts/jNQXAC9IVRw")]
    [InlineData("https://www.youtube.com/live/jNQXAC9IVRw?feature=share")]
    [InlineData("https://www.youtube.com/embed/jNQXAC9IVRw")]
    [InlineData("https://www.youtube-nocookie.com/embed/jNQXAC9IVRw")]
    public void ExtractsTheIdFromEveryCommonForm(string input)
    {
        Assert.True(VideoIdParser.TryExtract(input, out var id));
        Assert.Equal("jNQXAC9IVRw", id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("me at the zoo")]
    [InlineData("jNQXAC9IVR")]
    [InlineData("jNQXAC9IVRw1")]
    [InlineData("https://vimeo.com/watch?v=jNQXAC9IVRw")]
    [InlineData("https://www.youtube.com.evil.example/watch?v=jNQXAC9IVRw")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://youtube.com/watch?v=jNQXAC9IVRw")]
    [InlineData("https://www.youtube.com/channel/UC4QobU6STFB0P71PMvOGN5A")]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IV%3Cw")]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw%0A")]
    [InlineData("Check this https://youtu.be/jNQXAC9IVRw")]
    public void RejectsAnythingElse(string? input)
    {
        Assert.False(VideoIdParser.TryExtract(input, out _));
    }

    [Fact]
    public void RebuildsTheCanonicalUrl()
    {
        Assert.Equal("https://www.youtube.com/watch?v=jNQXAC9IVRw", VideoIdParser.WatchUrl("jNQXAC9IVRw"));
    }
}
