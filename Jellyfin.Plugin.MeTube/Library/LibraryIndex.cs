using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MeTube.Library;

/// <summary>
/// Knows which YouTube videos are already in the library, by the "[videoId]" that
/// MeTube's output template puts at the end of every file name. Built from the
/// folder itself rather than Jellyfin's database, so it also sees files Jellyfin
/// has not scanned yet. Rebuilt at most once a minute, and sooner when Jellyfin
/// adds or removes an item.
/// </summary>
public sealed partial class LibraryIndex : IDisposable
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);

    private readonly ILibraryManager _libraryManager;
    private readonly LibraryLocator _locator;
    private readonly ILogger<LibraryIndex> _logger;
    private readonly object _lock = new();

    private Dictionary<string, string> _byVideoId = new(StringComparer.Ordinal);
    private string? _builtFor;
    private DateTimeOffset _builtAt = DateTimeOffset.MinValue;
    private volatile bool _dirty = true;

    /// <summary>Initializes a new instance of the <see cref="LibraryIndex"/> class.</summary>
    public LibraryIndex(ILibraryManager libraryManager, LibraryLocator locator, ILogger<LibraryIndex> logger)
    {
        _libraryManager = libraryManager;
        _locator = locator;
        _logger = logger;
        _libraryManager.ItemAdded += OnLibraryChanged;
        _libraryManager.ItemRemoved += OnLibraryChanged;
    }

    /// <summary>Extracts the video id from a file name ending in "[id].ext".</summary>
    public static string? VideoIdFromFileName(string fileName)
    {
        var match = FileNameRegex().Match(fileName);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Finds the file for a video id, if it is in the library folder.</summary>
    public bool TryFindFile(string videoId, out string path)
    {
        var index = Snapshot();
        if (index.TryGetValue(videoId, out var found))
        {
            path = found;
            return true;
        }

        path = string.Empty;
        return false;
    }

    /// <summary>
    /// Returns the Jellyfin item for a video, trying the exact path MeTube reported
    /// first and then the "[id]" index. Null while Jellyfin has not scanned the file.
    /// </summary>
    public Guid? ResolveItemId(string videoId, string? expectedPath)
    {
        try
        {
            if (!string.IsNullOrEmpty(expectedPath))
            {
                var item = _libraryManager.FindByPath(expectedPath, false);
                if (item is not null)
                {
                    return item.Id;
                }
            }

            if (TryFindFile(videoId, out var path))
            {
                return _libraryManager.FindByPath(path, false)?.Id;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MeTube: item lookup for {VideoId} failed.", videoId);
        }

        return null;
    }

    /// <summary>Returns true while the Jellyfin item still exists (it may have been deleted).</summary>
    public bool ItemExists(Guid itemId)
    {
        try
        {
            return _libraryManager.GetItemById(itemId) is not null;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Marks the index stale so the next lookup rebuilds it.</summary>
    public void Invalidate() => _dirty = true;

    /// <inheritdoc />
    public void Dispose()
    {
        _libraryManager.ItemAdded -= OnLibraryChanged;
        _libraryManager.ItemRemoved -= OnLibraryChanged;
    }

    private Dictionary<string, string> Snapshot()
    {
        var location = _locator.Resolve();
        var root = location?.Path;
        lock (_lock)
        {
            var fresh = !_dirty
                && string.Equals(_builtFor, root, StringComparison.Ordinal)
                && DateTimeOffset.UtcNow - _builtAt < MaxAge;
            if (fresh)
            {
                return _byVideoId;
            }

            _dirty = false;
            _builtFor = root;
            _builtAt = DateTimeOffset.UtcNow;
            _byVideoId = root is null ? new Dictionary<string, string>(StringComparer.Ordinal) : Scan(root);
            return _byVideoId;
        }
    }

    private Dictionary<string, string> Scan(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!Directory.Exists(root))
            {
                return result;
            }

            // Hidden entries are skipped by default, and on Linux that means every
            // name starting with a dot, so MeTube's ".metube-tmp" is never indexed.
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                var videoId = VideoIdFromFileName(Path.GetFileName(file));
                if (videoId is not null)
                {
                    result[videoId] = file;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MeTube: could not scan {Root}.", root);
        }

        return result;
    }

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            var root = _builtFor;
            var path = e?.Item?.Path;
            if (root is null || path is null || path.StartsWith(root, StringComparison.Ordinal))
            {
                _dirty = true;
            }
        }
        catch (Exception)
        {
            // An exception escaping a library event handler would surface inside
            // Jellyfin's own scan. Never let a cache invalidation do that.
            _dirty = true;
        }
    }

    [GeneratedRegex(@"\[([A-Za-z0-9_-]{11})\]\.(?:mkv|mp4|webm|m4v|mov|avi)$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNameRegex();
}
