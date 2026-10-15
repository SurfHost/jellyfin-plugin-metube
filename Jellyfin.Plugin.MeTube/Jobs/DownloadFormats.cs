using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.MeTube.MeTube;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MeTube.Jobs;

/// <summary>The download formats a user can pick on the YouTube page.</summary>
public static class DownloadFormats
{
    /// <summary>Use the format from the plugin settings.</summary>
    public const string Default = "default";

    /// <summary>H.264 + AAC in MP4, up to 1080p: plays on every Chromecast without transcoding.</summary>
    public const string Compatible = "compatible";

    /// <summary>The best quality YouTube has (often 4K AV1 in MKV).</summary>
    public const string Best = "best";

    /// <summary>Returns the choice if it is a known one, otherwise <see cref="Default"/>.</summary>
    public static string Normalize(string? choice) =>
        choice?.Trim().ToLowerInvariant() switch
        {
            Compatible => Compatible,
            Best => Best,
            _ => Default
        };

    /// <summary>Returns true for a value a client may send.</summary>
    public static bool IsKnown(string? choice) =>
        choice?.Trim().ToLowerInvariant() is Default or Compatible or Best;

    /// <summary>Applies a user's choice to the MeTube options from the plugin settings.</summary>
    public static MeTubeAddOptions Apply(MeTubeAddOptions options, string? choice) =>
        Normalize(choice) switch
        {
            Compatible => options with { Format = "ios", Quality = "best", Codec = "auto" },
            Best => options with { Format = "any", Quality = "best", Codec = "auto" },
            _ => options
        };
}

/// <summary>
/// Remembers each user's download format, so the choice follows them to every device.
/// Kept next to the job list, as a small JSON file.
/// </summary>
public sealed class UserPreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly string _path;
    private readonly ILogger<UserPreferenceStore> _logger;
    private readonly object _lock = new();
    private Dictionary<Guid, string>? _formats;

    /// <summary>Initializes a new instance of the <see cref="UserPreferenceStore"/> class.</summary>
    public UserPreferenceStore(IApplicationPaths applicationPaths, ILogger<UserPreferenceStore> logger)
        : this(Path.Combine(applicationPaths.DataPath, "metube"), logger)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="UserPreferenceStore"/> class at an explicit folder (tests).</summary>
    public UserPreferenceStore(string directory, ILogger<UserPreferenceStore> logger)
    {
        _directory = directory;
        _path = Path.Combine(directory, "user-formats.json");
        _logger = logger;
    }

    /// <summary>Returns the user's download format, <see cref="DownloadFormats.Default"/> when none was picked.</summary>
    public string GetFormat(Guid userId)
    {
        lock (_lock)
        {
            return Load().TryGetValue(userId, out var format) ? format : DownloadFormats.Default;
        }
    }

    /// <summary>Stores the user's download format; <see cref="DownloadFormats.Default"/> forgets it.</summary>
    public void SetFormat(Guid userId, string? format)
    {
        var normalized = DownloadFormats.Normalize(format);
        lock (_lock)
        {
            var formats = Load();
            var changed = normalized == DownloadFormats.Default
                ? formats.Remove(userId)
                : !(formats.TryGetValue(userId, out var current) && current == normalized);
            if (normalized != DownloadFormats.Default)
            {
                formats[userId] = normalized;
            }

            if (changed)
            {
                Directory.CreateDirectory(_directory);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(formats, JsonOptions));
                File.Move(temp, _path, overwrite: true);
            }
        }
    }

    private Dictionary<Guid, string> Load()
    {
        if (_formats is not null)
        {
            return _formats;
        }

        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<Guid, string>>(File.ReadAllText(_path), JsonOptions);
                _formats = (loaded ?? new Dictionary<Guid, string>())
                    .Where(p => DownloadFormats.Normalize(p.Value) != DownloadFormats.Default)
                    .ToDictionary(p => p.Key, p => DownloadFormats.Normalize(p.Value));
                return _formats;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(ex, "MeTube: could not read {Path}; every user gets the default download format.", _path);
        }

        _formats = new Dictionary<Guid, string>();
        return _formats;
    }
}
