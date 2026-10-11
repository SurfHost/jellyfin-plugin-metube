using System;
using System.Threading;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MeTube.Library;

/// <summary>
/// Queues a scan of just the configured library, the same thing the dashboard's
/// "Scan library" does, so a finished download shows up within seconds instead of
/// waiting for real-time monitoring or the scheduled scan.
/// </summary>
public sealed class LibraryRefresher
{
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<LibraryRefresher> _logger;
    private long _lastThrottledTicks;

    /// <summary>Initializes a new instance of the <see cref="LibraryRefresher"/> class.</summary>
    public LibraryRefresher(IProviderManager providerManager, IFileSystem fileSystem, ILogger<LibraryRefresher> logger)
    {
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Queues a refresh at most once per <paramref name="minInterval"/>, for files that sit
    /// in the folder without a Jellyfin item (downloaded outside the plugin, or a scan that
    /// missed them). Search and Add notice those; this nudges Jellyfin without letting
    /// every request start a scan.
    /// </summary>
    public void QueueRefreshThrottled(Guid libraryId, TimeSpan minInterval)
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var last = Interlocked.Read(ref _lastThrottledTicks);
        if (now - last < minInterval.Ticks || Interlocked.CompareExchange(ref _lastThrottledTicks, now, last) != last)
        {
            return;
        }

        QueueRefresh(libraryId);
    }

    /// <summary>Queues a refresh of the library with the given item id.</summary>
    public void QueueRefresh(Guid libraryId)
    {
        try
        {
            // A fresh DirectoryService every time: it caches folder listings, and a
            // reused one would not see the file that was just downloaded.
            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.Default,
                ImageRefreshMode = MetadataRefreshMode.Default
            };

            _providerManager.QueueRefresh(libraryId, options, RefreshPriority.High);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MeTube: could not queue a refresh of library {LibraryId}.", libraryId);
        }
    }
}
