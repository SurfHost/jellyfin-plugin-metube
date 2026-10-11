using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MeTube.Library;

/// <summary>How a delete ended.</summary>
public enum DeleteOutcome
{
    /// <summary>The video and its sidecar files are gone.</summary>
    Deleted,

    /// <summary>No file for the video was found in the library folder.</summary>
    NotFound,

    /// <summary>Jellyfin is not allowed to delete files in the library folder.</summary>
    NotWritable,

    /// <summary>The file failed a safety check and was left alone.</summary>
    Refused,

    /// <summary>The library folder is not configured or not reachable right now.</summary>
    Unavailable
}

/// <summary>Result of <see cref="VideoDeleter.Delete"/>.</summary>
public sealed record DeleteResult(DeleteOutcome Outcome, string? Message, string? DeletedPath);

/// <summary>
/// Deletes a downloaded video from the library: the video file, its sidecars (poster,
/// subtitles, info), the Jellyfin item, and the channel folder once it is empty.
/// <para>
/// Jellyfin's own "delete file" option is deliberately not used: for a video that is
/// alone in its folder, Jellyfin deletes the whole folder recursively. This class only
/// deletes files whose name is the video's own name plus an extension, after checking
/// that the file is inside the configured library and carries the expected video id.
/// </para>
/// </summary>
public sealed class VideoDeleter
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".webm", ".m4v", ".mov", ".avi"
    };

    private readonly ILibraryManager _libraryManager;
    private readonly LibraryLocator _locator;
    private readonly LibraryIndex _libraryIndex;
    private readonly ILogger<VideoDeleter> _logger;

    /// <summary>Initializes a new instance of the <see cref="VideoDeleter"/> class.</summary>
    public VideoDeleter(ILibraryManager libraryManager, LibraryLocator locator, LibraryIndex libraryIndex, ILogger<VideoDeleter> logger)
    {
        _libraryManager = libraryManager;
        _locator = locator;
        _libraryIndex = libraryIndex;
        _logger = logger;
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> lies inside <paramref name="libraryRoot"/>
    /// and its file name ends in "[videoId].ext".
    /// </summary>
    public static bool IsSafeTarget(string libraryRoot, string path, string videoId)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var root = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        return full.StartsWith(root, StringComparison.Ordinal)
            && string.Equals(LibraryIndex.VideoIdFromFileName(Path.GetFileName(full)), videoId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns true for a file that belongs to the video with this file stem:
    /// "stem.jpg", "stem.nl.vtt", "stem.info.json", or Jellyfin's "stem-poster.jpg".
    /// </summary>
    public static bool IsOwnFile(string stem, string fileName) =>
        fileName.StartsWith(stem + ".", StringComparison.Ordinal) || fileName.StartsWith(stem + "-", StringComparison.Ordinal);

    /// <summary>
    /// Deletes the video with this id from the configured library. When the video is in
    /// the library twice, <paramref name="preferredRelative"/> (the file the owner's job
    /// recorded, relative to the library) picks the copy to delete.
    /// </summary>
    public DeleteResult Delete(string videoId, string? preferredRelative = null)
    {
        var library = _locator.Resolve();
        if (library is null || !Directory.Exists(library.Path))
        {
            // Not "not found": the file may well still be there once the folder is back.
            return new DeleteResult(DeleteOutcome.Unavailable, "The YouTube library is not available right now. Try again later.", null);
        }

        _libraryIndex.Invalidate();
        string path;
        if (PathMapper.TryMap(library.Path, preferredRelative, out var own) && File.Exists(own))
        {
            path = own;
        }
        else if (!_libraryIndex.TryFindFile(videoId, out path))
        {
            return new DeleteResult(DeleteOutcome.NotFound, "This video is no longer in the library.", null);
        }

        if (!IsSafeTarget(library.Path, path, videoId))
        {
            _logger.LogWarning("MeTube: refused to delete {Path}: outside {Root} or not video {VideoId}.", path, library.Path, videoId);
            return new DeleteResult(DeleteOutcome.Refused, "The file is not where the plugin expects it, so it was left alone.", null);
        }

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        var stem = Path.GetFileNameWithoutExtension(full);

        // The video file itself first: if Jellyfin may not delete it, stop before
        // touching anything else so the library stays consistent.
        try
        {
            File.Delete(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "MeTube: could not delete {Path}.", full);
            return new DeleteResult(
                DeleteOutcome.NotWritable,
                "Jellyfin is not allowed to delete files in the YouTube folder. Give the Jellyfin server write access to that folder (on TrueNAS: untick Read Only on the Jellyfin app's storage).",
                null);
        }

        var removedVideos = new List<string> { full };
        foreach (var file in SafeEnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            if (!IsOwnFile(stem, name))
            {
                continue;
            }

            try
            {
                File.Delete(file);
                if (VideoExtensions.Contains(Path.GetExtension(name)))
                {
                    removedVideos.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "MeTube: could not delete sidecar {Path}.", file);
            }
        }

        foreach (var video in removedVideos)
        {
            RemoveItem(_libraryManager.FindByPath(video, false));
        }

        RemoveEmptyFolder(directory, library.Path);
        _libraryIndex.Invalidate();
        _logger.LogInformation("MeTube: deleted {Path} and its sidecar files.", full);
        return new DeleteResult(DeleteOutcome.Deleted, null, full);
    }

    /// <summary>
    /// Checks that Jellyfin can create and delete files in the library folder and in one
    /// channel folder (MeTube creates those, possibly with different permissions).
    /// </summary>
    public (bool Ok, string Detail) ProbeWritable()
    {
        var library = _locator.Resolve();
        if (library is null || !Directory.Exists(library.Path))
        {
            return (false, "The library folder is not available.");
        }

        var targets = new List<string> { library.Path };
        var channel = SafeEnumerateDirectories(library.Path).FirstOrDefault(d => !Path.GetFileName(d).StartsWith('.'));
        if (channel is not null)
        {
            targets.Add(channel);
        }

        foreach (var folder in targets)
        {
            var probe = Path.Combine(folder, ".mtj-write-test");
            try
            {
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (false, "Jellyfin cannot write to " + folder + ", so Delete will not work: " + ex.Message);
            }
        }

        return (true, "Jellyfin can delete videos in " + library.Path + ".");
    }

    private static IEnumerable<string> SafeEnumerateFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private void RemoveItem(MediaBrowser.Controller.Entities.BaseItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            // DeleteFileLocation stays false: the files are already gone, and with true
            // Jellyfin would delete the containing folder of a single video.
            _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, true);
        }
        catch (Exception ex)
        {
            // The next library scan removes the item anyway.
            _logger.LogWarning(ex, "MeTube: could not remove item {Name} from the library database.", item.Name);
        }
    }

    private void RemoveEmptyFolder(string directory, string libraryRoot)
    {
        var root = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any())
            {
                return;
            }

            var folderItem = _libraryManager.FindByPath(directory, true);
            Directory.Delete(directory, recursive: false);
            RemoveItem(folderItem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Something appeared in the folder, or it cannot be removed; leave it.
            _logger.LogDebug(ex, "MeTube: kept folder {Directory}.", directory);
        }
    }
}
