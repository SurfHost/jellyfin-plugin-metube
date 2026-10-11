using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.MeTube.Library;

/// <summary>
/// Maps the file name MeTube reports (relative to its download folder, for example
/// "Channel/Title [id].mkv") onto the same file as Jellyfin sees it.
/// </summary>
public static class PathMapper
{
    /// <summary>
    /// Joins the library folder and MeTube's relative file name. Refuses absolute
    /// paths and "." or ".." segments, so a hostile or broken MeTube answer can
    /// never point the plugin outside the library folder.
    /// </summary>
    public static bool TryMap(string? libraryPath, string? relativeFile, [NotNullWhen(true)] out string? fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(libraryPath) || string.IsNullOrWhiteSpace(relativeFile))
        {
            return false;
        }

        if (relativeFile.StartsWith('/') || relativeFile.StartsWith('\\') || Path.IsPathRooted(relativeFile) || relativeFile.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = relativeFile.Split('/', '\\');
        if (segments.Any(s => s.Length == 0 || s == "." || s == ".."))
        {
            return false;
        }

        fullPath = Path.Combine(new[] { libraryPath }.Concat(segments).ToArray());
        return true;
    }
}
