using System;
using System.IO;
using Jellyfin.Plugin.MeTube.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MeTube.Tests;

public class LetterboxTests
{
    [Fact]
    public void ReadsTheSizeFromAJpegHeader()
    {
        // SOI, APP0 (length 16), SOF0 with height 480 and width 640.
        var bytes = new byte[]
        {
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0xE0, 0x02, 0x80, 0x03
        };
        using var stream = new MemoryStream(bytes);

        Assert.Equal((640, 480), LibraryMaintenance.ReadJpegSize(stream));
    }

    [Fact]
    public void RefusesSomethingThatIsNotAJpeg()
    {
        using var stream = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        Assert.Null(LibraryMaintenance.ReadJpegSize(stream));
    }

    [Theory]
    [InlineData(640, 480, 360)]
    [InlineData(480, 360, 270)]
    [InlineData(1280, 720, null)]
    [InlineData(1280, 724, null)]
    public void OnlyTallerThanWidescreenImagesAreCandidates(int width, int height, int? expected)
    {
        Assert.Equal(expected, LibraryMaintenance.CropHeightFor(width, height));
    }

    [Fact]
    public void ReadsTheLastCropdetectVerdict()
    {
        var output = "[Parsed_cropdetect_0 @ 0x1] x1:0 x2:639 y1:58 y2:421 w:640 h:364 x:0 y:58 pts:2 t:0.08 crop=640:364:0:58\n"
            + "[Parsed_cropdetect_0 @ 0x1] x1:0 x2:639 y1:60 y2:419 w:640 h:360 x:0 y:60 pts:3 t:0.12 crop=640:360:0:60\n";

        Assert.Equal(new CropBox(640, 360, 0, 60), LibraryMaintenance.ParseCropDetect(output));
        Assert.Null(LibraryMaintenance.ParseCropDetect("no verdict"));
    }

    [Fact]
    public void CropsAWidescreenPictureInsideBlackBars()
    {
        Assert.Equal(360, LibraryMaintenance.LetterboxCropHeight(640, 480, new CropBox(640, 360, 0, 60)));
    }

    [Fact]
    public void LeavesAFourByThreePictureAlone()
    {
        Assert.Null(LibraryMaintenance.LetterboxCropHeight(640, 480, new CropBox(640, 480, 0, 0)));
    }

    [Fact]
    public void LeavesADarkPictureWithUnevenBordersAlone()
    {
        Assert.Null(LibraryMaintenance.LetterboxCropHeight(640, 480, new CropBox(640, 200, 0, 20)));
        Assert.Null(LibraryMaintenance.LetterboxCropHeight(640, 480, new CropBox(400, 360, 120, 60)));
    }
}

public class FolderNameTests
{
    [Theory]
    [InlineData("Backstreet Boys", true)]
    [InlineData("jawed", true)]
    [InlineData(".metube-tmp", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AcceptsOnlyPlainVisibleNames(string? name, bool expected)
    {
        Assert.Equal(expected, FolderNames.IsSafeName(name));
    }

    [Fact]
    public void HiddenNamesCanBeAllowedForTheTempFolder()
    {
        Assert.True(FolderNames.IsSafeName(".metube-tmp", allowHidden: true));
        Assert.False(FolderNames.IsSafeName("../x", allowHidden: true));
    }

    [Fact]
    public void ChildOfStaysInsideTheRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mtj-root");
        Assert.Equal(Path.Combine(root, "jawed"), FolderNames.ChildOf(root, "jawed"));
        Assert.Null(FolderNames.ChildOf(root, ".."));
        Assert.Null(FolderNames.ChildOf(root, "a/../../b"));
    }
}

public sealed class FolderAliasStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtj-alias-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void ResolvesMergesOfMergesAndSurvivesARestart()
    {
        var store = NewStore();
        store.Merge(new[] { "A", "B" }, "B");
        store.Merge(new[] { "B", "C" }, "C");

        var reloaded = NewStore();
        Assert.Equal("C", reloaded.Resolve("A"));
        Assert.Equal("C", reloaded.Resolve("B"));
        Assert.Null(reloaded.Resolve("C"));
        Assert.Null(reloaded.Resolve("D"));
    }

    [Fact]
    public void TheKeptFolderStopsBeingAnAlias()
    {
        var store = NewStore();
        store.Merge(new[] { "A" }, "B");
        store.Merge(new[] { "B" }, "A");

        Assert.Equal("A", store.Resolve("B"));
        Assert.Null(store.Resolve("A"));
    }

    [Fact]
    public void ForgetsAMergeOnRequest()
    {
        var store = NewStore();
        store.Merge(new[] { "A" }, "B");

        Assert.True(store.Remove("A"));
        Assert.False(store.Remove("A"));
        Assert.Null(store.Resolve("A"));
    }

    private FolderAliasStore NewStore() => new(_dir, NullLogger<FolderAliasStore>.Instance);
}
