using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MeTube.Library;

/// <summary>A rectangle reported by ffmpeg's cropdetect: the part of the image that is not black.</summary>
public readonly record struct CropBox(int Width, int Height, int X, int Y);

/// <summary>
/// Housekeeping for the YouTube library folder:
/// <list type="bullet">
/// <item>removes the empty channel folders yt-dlp leaves behind in MeTube's temp folder;</item>
/// <item>crops letterboxed thumbnails to 16:9. When a video has no HD thumbnail, yt-dlp
/// falls back to YouTube's 640x480 one, which pads 16:9 video with black bars; Jellyfin
/// then shows those bars on the library's tile.</item>
/// </list>
/// </summary>
public sealed partial class LibraryMaintenance
{
    /// <summary>Temp folders younger than this are left alone, in case a download just created them.</summary>
    public static readonly TimeSpan TempFolderGrace = TimeSpan.FromMinutes(2);

    /// <summary>Thumbnails changed more recently than this are left for the next sweep.</summary>
    public static readonly TimeSpan ThumbnailGrace = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromSeconds(30);

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly LibraryLocator _locator;
    private readonly ILogger<LibraryMaintenance> _logger;

    // Thumbnails already judged, with the write time they had then: each file is
    // looked at once, not on every sweep.
    private readonly ConcurrentDictionary<string, DateTime> _checked = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="LibraryMaintenance"/> class.</summary>
    public LibraryMaintenance(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        IMediaEncoder mediaEncoder,
        LibraryLocator locator,
        ILogger<LibraryMaintenance> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _mediaEncoder = mediaEncoder;
        _locator = locator;
        _logger = logger;
    }

    /// <summary>Reads the pixel size from a JPEG header; null when the file is not a readable JPEG.</summary>
    public static (int Width, int Height)? ReadJpegSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return ReadJpegSize(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads the pixel size from a JPEG stream; null when it is not a readable JPEG.</summary>
    public static (int Width, int Height)? ReadJpegSize(Stream stream)
    {
        try
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            if (reader.ReadByte() != 0xFF || reader.ReadByte() != 0xD8)
            {
                return null;
            }

            while (true)
            {
                if (reader.ReadByte() != 0xFF)
                {
                    continue;
                }

                var marker = reader.ReadByte();
                while (marker == 0xFF)
                {
                    marker = reader.ReadByte();
                }

                if (marker is 0x00 or 0x01 or (>= 0xD0 and <= 0xD8))
                {
                    continue; // Stuffed byte, or a marker without a length.
                }

                var length = (reader.ReadByte() << 8) | reader.ReadByte();
                if (length < 2)
                {
                    return null;
                }

                // SOF0..SOF15, except DHT (C4), JPG (C8) and DAC (CC), carry the frame size.
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    reader.ReadByte(); // Sample precision.
                    var height = (reader.ReadByte() << 8) | reader.ReadByte();
                    var width = (reader.ReadByte() << 8) | reader.ReadByte();
                    return width > 0 && height > 0 ? (width, height) : null;
                }

                stream.Seek(length - 2, SeekOrigin.Current);
            }
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the 16:9 height for an image of this size when it is clearly taller than
    /// 16:9, or null when it is already 16:9 or wider. Even, for ffmpeg.
    /// </summary>
    public static int? CropHeightFor(int width, int height)
    {
        var target = width * 9 / 16 / 2 * 2;
        return target > 0 && height - target >= 8 ? target : null;
    }

    /// <summary>Returns the last "crop=w:h:x:y" in ffmpeg's cropdetect output.</summary>
    public static CropBox? ParseCropDetect(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        var matches = CropRegex().Matches(output);
        if (matches.Count == 0)
        {
            return null;
        }

        var last = matches[^1];
        return new CropBox(
            int.Parse(last.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(last.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(last.Groups[3].Value, CultureInfo.InvariantCulture),
            int.Parse(last.Groups[4].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Decides whether a thumbnail is letterboxed: black bands of about equal height at the
    /// top and bottom, across the full width, around a picture no taller than 16:9. Returns
    /// the 16:9 height to crop to, or null to leave the image alone (a 4:3 video's own
    /// thumbnail, or a dark picture that only looks like bars).
    /// </summary>
    public static int? LetterboxCropHeight(int width, int height, CropBox content)
    {
        if (CropHeightFor(width, height) is not { } target)
        {
            return null;
        }

        var tolerance = Math.Max(8, height / 32);
        var top = content.Y;
        var bottom = height - content.Y - content.Height;
        var fullWidth = content.Width >= width - (2 * tolerance);
        var symmetric = top >= 8 && bottom >= 8 && Math.Abs(top - bottom) <= tolerance;
        var plausible = content.Height <= target + tolerance && content.Height >= target / 2;
        return fullWidth && symmetric && plausible ? target : null;
    }

    /// <summary>
    /// Removes empty folders inside MeTube's temp folder. Never deletes files, never the
    /// temp folder itself, and leaves folders younger than <see cref="TempFolderGrace"/>.
    /// Call only while MeTube's queue is empty.
    /// </summary>
    public int CleanTempFolders()
    {
        var library = _locator.Resolve();
        var name = (Plugin.Config.TempFolderName ?? string.Empty).Trim();
        if (library is null || !FolderNames.IsSafeName(name, allowHidden: true))
        {
            return 0;
        }

        var temp = Path.Combine(library.Path, name);
        if (!Directory.Exists(temp))
        {
            return 0;
        }

        var removed = 0;
        try
        {
            // Deepest first, so a folder that only holds empty folders goes too.
            var folders = Directory.EnumerateDirectories(temp, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
                .OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar))
                .ToList();
            foreach (var folder in folders)
            {
                try
                {
                    var info = new DirectoryInfo(folder);
                    if (info.LinkTarget is not null
                        || DateTime.UtcNow - info.LastWriteTimeUtc < TempFolderGrace
                        || info.EnumerateFileSystemInfos().Any())
                    {
                        continue;
                    }

                    info.Delete(recursive: false);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "MeTube: could not remove temp folder {Folder}.", folder);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "MeTube: could not scan {Temp}.", temp);
        }

        if (removed > 0)
        {
            _logger.LogInformation("MeTube: removed {Count} empty folder(s) from {Temp}.", removed, temp);
        }

        return removed;
    }

    /// <summary>
    /// Crops the thumbnail next to a video when it is letterboxed. Returns true when the
    /// image was changed.
    /// </summary>
    public async Task<bool> FixThumbnailAsync(string videoPath, CancellationToken cancellationToken)
    {
        var thumbnail = Path.ChangeExtension(videoPath, ".jpg");
        DateTime written;
        try
        {
            if (!File.Exists(thumbnail))
            {
                return false;
            }

            written = File.GetLastWriteTimeUtc(thumbnail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (_checked.TryGetValue(thumbnail, out var seen) && seen == written)
        {
            return false;
        }

        var changed = await CropIfLetterboxedAsync(thumbnail, cancellationToken).ConfigureAwait(false);
        try
        {
            _checked[thumbnail] = File.GetLastWriteTimeUtc(thumbnail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _checked.TryRemove(thumbnail, out _);
        }

        return changed;
    }

    /// <summary>
    /// Crops every letterboxed thumbnail in the library, then refreshes those videos'
    /// images and rebuilds the library's tile, which Jellyfin otherwise keeps as it is.
    /// Returns how many thumbnails were fixed.
    /// </summary>
    public async Task<int> SweepThumbnailsAsync(CancellationToken cancellationToken)
    {
        var library = _locator.Resolve();
        if (library is null || !Directory.Exists(library.Path) || !CanRunFfmpeg())
        {
            return 0;
        }

        var fixedVideos = new List<string>();
        foreach (var video in EnumerateVideos(library.Path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var thumbnail = Path.ChangeExtension(video, ".jpg");
            try
            {
                if (!File.Exists(thumbnail) || DateTime.UtcNow - File.GetLastWriteTimeUtc(thumbnail) < ThumbnailGrace)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (await FixThumbnailAsync(video, cancellationToken).ConfigureAwait(false))
            {
                fixedVideos.Add(video);
            }
        }

        if (fixedVideos.Count > 0)
        {
            foreach (var video in fixedVideos)
            {
                await RefreshImagesAsync(_libraryManager.FindByPath(video, false), regenerate: false, cancellationToken).ConfigureAwait(false);
            }

            await RefreshLibraryImageAsync(cancellationToken).ConfigureAwait(false);
        }

        return fixedVideos.Count;
    }

    /// <summary>Rebuilds the library's own tile (a collage of its videos' images).</summary>
    public async Task RefreshLibraryImageAsync(CancellationToken cancellationToken)
    {
        var library = _locator.Resolve();
        if (library is not null)
        {
            await RefreshImagesAsync(_libraryManager.GetItemById(library.Id), regenerate: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IEnumerable<string> EnumerateVideos(string root)
    {
        try
        {
            // Hidden entries are skipped by default, which on Linux means MeTube's
            // ".metube-tmp" with its half-finished downloads.
            return Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Where(f => LibraryIndex.VideoIdFromFileName(Path.GetFileName(f)) is not null)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort only; Jellyfin ignores the dot-file anyway.
        }
    }

    [GeneratedRegex(@"crop=(\d+):(\d+):(\d+):(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex CropRegex();

    private bool CanRunFfmpeg()
    {
        var ffmpeg = _mediaEncoder.EncoderPath;
        return !string.IsNullOrEmpty(ffmpeg) && File.Exists(ffmpeg);
    }

    private async Task<bool> CropIfLetterboxedAsync(string thumbnail, CancellationToken cancellationToken)
    {
        // The header check is free and rules out every 16:9 thumbnail; only 4:3 ones
        // are handed to ffmpeg to look for bars.
        if (ReadJpegSize(thumbnail) is not { } size || CropHeightFor(size.Width, size.Height) is null || !CanRunFfmpeg())
        {
            return false;
        }

        // A still image is one frame, and cropdetect skips the first frames by default:
        // loop it a few times so a verdict is printed.
        var detect = await RunFfmpegAsync(
            new[] { "-hide_banner", "-nostats", "-loglevel", "info", "-loop", "1", "-i", thumbnail, "-vf", "cropdetect=limit=24:round=2:reset=0", "-frames:v", "5", "-f", "null", "-" },
            cancellationToken).ConfigureAwait(false);
        if (detect is null || ParseCropDetect(detect.Value.Output) is not { } content
            || LetterboxCropHeight(size.Width, size.Height, content) is not { } cropHeight)
        {
            return false;
        }

        var output = Path.Combine(Path.GetDirectoryName(thumbnail)!, ".mtj-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            var crop = await RunFfmpegAsync(
                new[] { "-hide_banner", "-nostats", "-loglevel", "error", "-y", "-i", thumbnail, "-vf", string.Create(CultureInfo.InvariantCulture, $"crop=iw:{cropHeight}"), "-q:v", "2", "-frames:v", "1", output },
                cancellationToken).ConfigureAwait(false);
            if (crop is null || crop.Value.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
            {
                _logger.LogWarning("MeTube: ffmpeg could not crop {Thumbnail}: {Error}", thumbnail, crop?.Output);
                return false;
            }

            File.Move(output, thumbnail, overwrite: true);
            _logger.LogInformation("MeTube: removed the black bars from {Thumbnail} ({Width}x{Height} to {Width}x{CropHeight}).", thumbnail, size.Width, size.Height, size.Width, cropHeight);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "MeTube: could not replace {Thumbnail}.", thumbnail);
            return false;
        }
        finally
        {
            TryDelete(output);
        }
    }

    private async Task<(int ExitCode, string Output)?> RunFfmpegAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_mediaEncoder.EncoderPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FfmpegTimeout);
        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await stderr.ConfigureAwait(false);
            await stdout.ConfigureAwait(false);
            return (process.ExitCode, output);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("MeTube: ffmpeg took longer than {Seconds} seconds; stopped it.", FfmpegTimeout.TotalSeconds);
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "MeTube: could not run ffmpeg.");
            return null;
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone.
                }

                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Re-reads an item's images. A video picks up its changed thumbnail from image
    /// validation alone. The library's tile is made by a metadata provider that only
    /// runs on a full metadata refresh (<paramref name="regenerate"/>); that refresh
    /// covers just the library item itself, not its contents.
    /// </summary>
    private async Task RefreshImagesAsync(BaseItem? item, bool regenerate, CancellationToken cancellationToken)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = regenerate ? MetadataRefreshMode.FullRefresh : MetadataRefreshMode.None,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly
            };
            await _providerManager.RefreshSingleItem(item, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MeTube: could not refresh the images of {Name}.", item.Name);
        }
    }
}
