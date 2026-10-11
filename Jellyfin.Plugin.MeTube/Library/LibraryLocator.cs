using System;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MeTube.Library;

/// <summary>The configured Jellyfin library, as Jellyfin sees it.</summary>
public sealed record LibraryLocation(Guid Id, string Name, string Path);

/// <summary>Resolves the configured library id to its name and folder.</summary>
public sealed class LibraryLocator
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryLocator> _logger;
    private readonly object _lock = new();
    private (string ConfigKey, DateTimeOffset At, LibraryLocation? Location)? _cache;

    /// <summary>Initializes a new instance of the <see cref="LibraryLocator"/> class.</summary>
    public LibraryLocator(ILibraryManager libraryManager, ILogger<LibraryLocator> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Returns the configured library, or null when none is configured or it no
    /// longer exists. When a library has several folders, the one named like the
    /// MeTube folder wins.
    /// </summary>
    public LibraryLocation? Resolve()
    {
        var config = Plugin.Config;
        var key = config.LibraryId + "|" + config.Folder;
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            if (_cache is { } cached && cached.ConfigKey == key && now - cached.At < CacheFor)
            {
                return cached.Location;
            }
        }

        LibraryLocation? location = null;
        if (Guid.TryParse(config.LibraryId, out var libraryId))
        {
            try
            {
                var folder = _libraryManager.GetVirtualFolders()
                    .FirstOrDefault(f => Guid.TryParse(f.ItemId, out var id) && id == libraryId);
                if (folder is not null && folder.Locations is { Length: > 0 })
                {
                    var path = folder.Locations.FirstOrDefault(l =>
                            string.Equals(System.IO.Path.GetFileName(l.TrimEnd('/', '\\')), config.Folder, StringComparison.OrdinalIgnoreCase))
                        ?? folder.Locations[0];
                    location = new LibraryLocation(libraryId, folder.Name, path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MeTube: could not read the library list.");
            }
        }

        lock (_lock)
        {
            _cache = (key, now, location);
        }

        return location;
    }

    /// <summary>Forgets the cached library so the next call re-reads it.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _cache = null;
        }
    }
}
