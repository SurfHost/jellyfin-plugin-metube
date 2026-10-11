using System;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.MeTube.Library;

/// <summary>Checks folder names that come from settings or from a request.</summary>
public static class FolderNames
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':' }).Distinct().ToArray();

    /// <summary>
    /// Returns true for a single folder name: not empty, no path separators or other
    /// characters a file name cannot hold, not "." or "..", and (unless
    /// <paramref name="allowHidden"/>) not hidden by a leading dot.
    /// </summary>
    public static bool IsSafeName(string? name, bool allowHidden = false)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".." || name.IndexOfAny(Invalid) >= 0)
        {
            return false;
        }

        return allowHidden || !name.StartsWith('.');
    }

    /// <summary>
    /// Returns the full path of <paramref name="name"/> as a direct child of
    /// <paramref name="root"/>, or null when the name is not safe or would land elsewhere.
    /// </summary>
    public static string? ChildOf(string root, string? name)
    {
        if (!IsSafeName(name))
        {
            return null;
        }

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(fullRoot, name!));
        return string.Equals(Path.GetDirectoryName(full), fullRoot, StringComparison.Ordinal) ? full : null;
    }
}
