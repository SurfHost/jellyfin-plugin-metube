using System;
using System.Threading;

namespace Jellyfin.Plugin.MeTube.Web;

/// <summary>Whether the menu-entry injection is working, for the settings page.</summary>
public sealed class WebInjectionStatus
{
    private long _served;

    /// <summary>Gets or sets a value indicating whether the startup filter was registered.</summary>
    public bool FilterInstalled { get; set; }

    /// <summary>Gets how many patched pages were served since startup.</summary>
    public long ServedCount => Interlocked.Read(ref _served);

    /// <summary>Gets or sets when a patched page was last served.</summary>
    public DateTimeOffset? LastServedUtc { get; set; }

    /// <summary>Gets or sets the last error, if any.</summary>
    public string? LastError { get; set; }

    /// <summary>Counts one served page.</summary>
    public void IncrementServed() => Interlocked.Increment(ref _served);
}
