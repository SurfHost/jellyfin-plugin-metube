using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Plugin.MeTube.Search;

/// <summary>
/// Protects the server's address from YouTube's bot detection. The NAS that searches
/// is also the NAS that downloads, so if YouTube starts throttling it, MeTube's
/// downloads suffer for everyone. Search and Add share one pause (started by any
/// 429/403 from YouTube) and per-user sliding-window limits.
/// </summary>
public sealed class YouTubeGuard
{
    /// <summary>How long to back off after YouTube answers 429 or 403.</summary>
    public static readonly TimeSpan PauseFor = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<(Guid User, string Bucket), Queue<DateTimeOffset>> _windows = new();
    private long _pausedUntilTicks;

    /// <summary>Returns true while YouTube calls are paused.</summary>
    public bool IsPaused(DateTimeOffset now) => now.UtcTicks < Interlocked.Read(ref _pausedUntilTicks);

    /// <summary>Starts (or extends) the pause.</summary>
    public void Pause(DateTimeOffset now) => Interlocked.Exchange(ref _pausedUntilTicks, (now + PauseFor).UtcTicks);

    /// <summary>Counts one action for a user, unless they already did <paramref name="perMinute"/> in the last minute.</summary>
    public bool TryCount(Guid userId, string bucket, int perMinute, DateTimeOffset now)
    {
        var window = _windows.GetOrAdd((userId, bucket), _ => new Queue<DateTimeOffset>());
        lock (window)
        {
            while (window.Count > 0 && now - window.Peek() > TimeSpan.FromMinutes(1))
            {
                window.Dequeue();
            }

            if (window.Count >= perMinute)
            {
                return false;
            }

            window.Enqueue(now);
            return true;
        }
    }
}
