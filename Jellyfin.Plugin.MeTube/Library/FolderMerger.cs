using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MeTube.Library;

/// <summary>
/// A channel folder in the library, as the Folders tab lists it. <paramref name="ItemId"/>
/// is the folder's own Jellyfin item, which is what Jellyfin's selection menu knows it by.
/// </summary>
public sealed record ChannelFolder(string Name, int VideoCount, Guid? SampleItemId, Guid? ItemId);

/// <summary>A video file that moved to another folder.</summary>
public sealed record MovedVideo(string VideoId, string RelativePath);

/// <summary>Result of <see cref="FolderMerger.Merge"/>.</summary>
public sealed class MergeResult
{
    /// <summary>Gets or sets the error that stopped the merge before anything moved; null when it ran.</summary>
    public string? Error { get; set; }

    /// <summary>Gets the videos that moved, with their new path relative to the library folder.</summary>
    public List<MovedVideo> Moved { get; } = new();

    /// <summary>Gets or sets how many files (videos and their thumbnails, subtitles...) moved.</summary>
    public int MovedFiles { get; set; }

    /// <summary>Gets the files left where they were because the kept folder already has one by that name.</summary>
    public List<string> Skipped { get; } = new();

    /// <summary>Gets the merged folders that were removed once empty.</summary>
    public List<string> RemovedFolders { get; } = new();
}

/// <summary>
/// Merges channel folders: moves every file from the chosen folders into the one to
/// keep, removes the emptied folders, and remembers the merge so later downloads from
/// those channels go into the kept folder as well.
/// <para>
/// Only direct children of the library folder are touched, never hidden folders (such
/// as MeTube's temp folder), never sub-folders, and an existing file is never
/// overwritten: a name clash leaves the file where it was.
/// </para>
/// </summary>
public sealed class FolderMerger
{
    private readonly ILibraryManager _libraryManager;
    private readonly LibraryLocator _locator;
    private readonly LibraryIndex _libraryIndex;
    private readonly FolderAliasStore _aliases;
    private readonly ILogger<FolderMerger> _logger;
    private readonly object _lock = new();

    /// <summary>Initializes a new instance of the <see cref="FolderMerger"/> class.</summary>
    public FolderMerger(ILibraryManager libraryManager, LibraryLocator locator, LibraryIndex libraryIndex, FolderAliasStore aliases, ILogger<FolderMerger> logger)
    {
        _libraryManager = libraryManager;
        _locator = locator;
        _libraryIndex = libraryIndex;
        _aliases = aliases;
        _logger = logger;
    }

    /// <summary>Lists the channel folders in the library, by name.</summary>
    public IReadOnlyList<ChannelFolder> ListFolders()
    {
        var library = _locator.Resolve();
        if (library is null || !Directory.Exists(library.Path))
        {
            return Array.Empty<ChannelFolder>();
        }

        var result = new List<ChannelFolder>();
        foreach (var directory in SafeEnumerateDirectories(library.Path))
        {
            var name = Path.GetFileName(directory);
            if (!FolderNames.IsSafeName(name))
            {
                continue;
            }

            var videos = SafeEnumerateFiles(directory)
                .Where(f => LibraryIndex.VideoIdFromFileName(Path.GetFileName(f)) is not null)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            Guid? sample = null;
            foreach (var video in videos.Take(3))
            {
                sample = FindItemId(video.FullName, isFolder: false);
                if (sample is not null)
                {
                    break;
                }
            }

            result.Add(new ChannelFolder(name, videos.Count, sample, FindItemId(directory, isFolder: true)));
        }

        return result.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Moves everything in <paramref name="folders"/> into <paramref name="target"/> (which
    /// may be listed among them) and records the merge for future downloads.
    /// </summary>
    public MergeResult Merge(IReadOnlyCollection<string> folders, string target)
    {
        var result = new MergeResult();
        var library = _locator.Resolve();
        if (library is null || !Directory.Exists(library.Path))
        {
            result.Error = "The YouTube library folder was not found.";
            return result;
        }

        var targetPath = FolderNames.ChildOf(library.Path, target);
        if (targetPath is null || !Directory.Exists(targetPath) || IsLink(targetPath))
        {
            result.Error = "The folder to keep does not exist.";
            return result;
        }

        var sources = new List<(string Name, string Path)>();
        foreach (var name in (folders ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal))
        {
            if (string.Equals(name, target, StringComparison.Ordinal))
            {
                continue;
            }

            var path = FolderNames.ChildOf(library.Path, name);
            if (path is null || !Directory.Exists(path) || IsLink(path))
            {
                result.Error = "The folder \"" + name + "\" does not exist.";
                return result;
            }

            sources.Add((name, path));
        }

        if (sources.Count == 0)
        {
            result.Error = "Choose at least two folders.";
            return result;
        }

        lock (_lock)
        {
            foreach (var source in sources)
            {
                MoveContents(library.Path, source.Path, targetPath, result);
                if (TryRemoveIfEmpty(source.Path))
                {
                    result.RemovedFolders.Add(source.Name);
                }
            }

            _aliases.Merge(sources.Select(s => s.Name), target);
        }

        _libraryIndex.Invalidate();
        _logger.LogInformation(
            "MeTube: merged {Sources} into {Target}: {Videos} video(s), {Files} file(s) moved, {Skipped} left because of a name clash.",
            string.Join(", ", sources.Select(s => s.Name)),
            target,
            result.Moved.Count,
            result.MovedFiles,
            result.Skipped.Count);
        return result;
    }

    /// <summary>
    /// For a download that just finished: when its channel folder was merged away, moves
    /// the video and its own sidecar files into the kept folder. Returns the new path
    /// relative to the library folder, or null when nothing moved.
    /// </summary>
    public string? ApplyAlias(string? relativeFile)
    {
        var library = _locator.Resolve();
        if (library is null || string.IsNullOrEmpty(relativeFile))
        {
            return null;
        }

        var segments = relativeFile.Split('/', '\\');
        if (segments.Length != 2 || _aliases.Resolve(segments[0]) is not { } target)
        {
            return null;
        }

        var sourcePath = FolderNames.ChildOf(library.Path, segments[0]);
        var targetPath = FolderNames.ChildOf(library.Path, target);
        if (sourcePath is null || targetPath is null || !PathMapper.TryMap(library.Path, relativeFile, out var video) || !File.Exists(video))
        {
            return null;
        }

        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(targetPath);
                var destination = Path.Combine(targetPath, segments[1]);
                if (File.Exists(destination))
                {
                    return null;
                }

                File.Move(video, destination, overwrite: false);
                var stem = Path.GetFileNameWithoutExtension(segments[1]);
                foreach (var file in SafeEnumerateFiles(sourcePath))
                {
                    var name = Path.GetFileName(file);
                    if (VideoDeleter.IsOwnFile(stem, name))
                    {
                        TryMove(file, Path.Combine(targetPath, name));
                    }
                }

                TryRemoveIfEmpty(sourcePath);
                _libraryIndex.Invalidate();
                _logger.LogInformation("MeTube: {File} belongs in the merged folder {Target}; moved it there.", relativeFile, target);
                return target + "/" + segments[1];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "MeTube: could not move {File} into the merged folder {Target}.", relativeFile, target);
                return null;
            }
        }
    }

    /// <summary>
    /// Moves files that arrived in a merged-away folder by other means (MeTube's own page,
    /// for example) into the kept folder. Call only while MeTube is idle.
    /// </summary>
    public IReadOnlyList<MovedVideo> SweepAliases()
    {
        var library = _locator.Resolve();
        var aliases = _aliases.GetAll();
        if (library is null || aliases.Count == 0)
        {
            return Array.Empty<MovedVideo>();
        }

        var result = new MergeResult();
        lock (_lock)
        {
            foreach (var source in aliases.Keys)
            {
                var sourcePath = FolderNames.ChildOf(library.Path, source);
                var target = _aliases.Resolve(source);
                var targetPath = target is null ? null : FolderNames.ChildOf(library.Path, target);
                if (sourcePath is null || targetPath is null || !Directory.Exists(sourcePath) || IsLink(sourcePath))
                {
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(targetPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "MeTube: could not create {Target}.", targetPath);
                    continue;
                }

                MoveContents(library.Path, sourcePath, targetPath, result);
                TryRemoveIfEmpty(sourcePath);
            }
        }

        if (result.MovedFiles > 0)
        {
            _libraryIndex.Invalidate();
            _logger.LogInformation("MeTube: moved {Count} new video(s) from merged channel folders into the folders they were merged into.", result.Moved.Count);
        }

        return result.Moved;
    }

    private static void MoveContents(string root, string sourcePath, string targetPath, MergeResult result)
    {
        var targetName = Path.GetFileName(targetPath);
        foreach (var file in SafeEnumerateFiles(sourcePath, includeHidden: true))
        {
            var name = Path.GetFileName(file);
            var destination = Path.Combine(targetPath, name);
            if (File.Exists(destination) || !TryMove(file, destination))
            {
                result.Skipped.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
                continue;
            }

            result.MovedFiles++;
            if (LibraryIndex.VideoIdFromFileName(name) is { } videoId)
            {
                result.Moved.Add(new MovedVideo(videoId, targetName + "/" + name));
            }
        }
    }

    private static bool TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryRemoveIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, recursive: false);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in place; the next merge or sweep tries again.
        }

        return false;
    }

    private static bool IsLink(string path)
    {
        try
        {
            return new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path, "*", new EnumerationOptions { IgnoreInaccessible = true }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path, bool includeHidden = false)
    {
        try
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true };
            if (includeHidden)
            {
                options.AttributesToSkip = 0;
            }

            return Directory.EnumerateFiles(path, "*", options).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private Guid? FindItemId(string path, bool isFolder)
    {
        try
        {
            return _libraryManager.FindByPath(path, isFolder)?.Id;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MeTube: item lookup for {Path} failed.", path);
            return null;
        }
    }
}
