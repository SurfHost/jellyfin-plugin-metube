using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MeTube.Library;

/// <summary>
/// Remembers merged channel folders: "videos that MeTube puts in folder A belong in
/// folder B". Kept in its own file next to the job list rather than in the plugin
/// settings, so saving the settings page can never undo a merge made meanwhile.
/// </summary>
public sealed class FolderAliasStore
{
    private const int MaxHops = 16;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly string _path;
    private readonly ILogger<FolderAliasStore> _logger;
    private readonly object _lock = new();
    private Dictionary<string, string>? _aliases;

    /// <summary>Initializes a new instance of the <see cref="FolderAliasStore"/> class.</summary>
    public FolderAliasStore(IApplicationPaths applicationPaths, ILogger<FolderAliasStore> logger)
        : this(Path.Combine(applicationPaths.DataPath, "metube"), logger)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="FolderAliasStore"/> class at an explicit folder (tests).</summary>
    public FolderAliasStore(string directory, ILogger<FolderAliasStore> logger)
    {
        _directory = directory;
        _path = Path.Combine(directory, "folder-aliases.json");
        _logger = logger;
    }

    /// <summary>Returns a copy of every alias, source folder to kept folder.</summary>
    public IReadOnlyDictionary<string, string> GetAll()
    {
        lock (_lock)
        {
            return new Dictionary<string, string>(Load(), StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Returns the folder that videos from <paramref name="folder"/> belong in, following
    /// merges of merges; null when the folder was never merged away.
    /// </summary>
    public string? Resolve(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        lock (_lock)
        {
            var aliases = Load();
            var current = folder;
            for (var hop = 0; hop < MaxHops && aliases.TryGetValue(current, out var next); hop++)
            {
                current = next;
            }

            return string.Equals(current, folder, StringComparison.Ordinal) ? null : current;
        }
    }

    /// <summary>
    /// Records that <paramref name="sources"/> were merged into <paramref name="target"/>.
    /// Earlier merges into a source now point at the target too, and the target itself
    /// stops being an alias: it is the folder that was kept.
    /// </summary>
    public void Merge(IEnumerable<string> sources, string target)
    {
        lock (_lock)
        {
            var aliases = Load();
            aliases.Remove(target);
            foreach (var source in sources.Where(s => !string.Equals(s, target, StringComparison.Ordinal)))
            {
                aliases[source] = target;
                foreach (var key in aliases.Where(a => string.Equals(a.Value, source, StringComparison.Ordinal)).Select(a => a.Key).ToList())
                {
                    aliases[key] = target;
                }
            }

            Save(aliases);
        }
    }

    /// <summary>Forgets one alias. Returns false when there was none.</summary>
    public bool Remove(string source)
    {
        lock (_lock)
        {
            var aliases = Load();
            if (!aliases.Remove(source))
            {
                return false;
            }

            Save(aliases);
            return true;
        }
    }

    private Dictionary<string, string> Load()
    {
        if (_aliases is not null)
        {
            return _aliases;
        }

        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path), JsonOptions);
                _aliases = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var pair in loaded ?? new Dictionary<string, string>())
                {
                    if (FolderNames.IsSafeName(pair.Key) && FolderNames.IsSafeName(pair.Value) && !string.Equals(pair.Key, pair.Value, StringComparison.Ordinal))
                    {
                        _aliases[pair.Key] = pair.Value;
                    }
                }

                return _aliases;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(ex, "MeTube: could not read {Path}; merged channels are forgotten until it is fixed.", _path);
        }

        _aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        return _aliases;
    }

    private void Save(Dictionary<string, string> aliases)
    {
        Directory.CreateDirectory(_directory);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(aliases, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
