using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MeTube.Configuration;

/// <summary>
/// Plugin configuration persisted by Jellyfin. Only administrators can read or
/// change it; the injected client script never sees it.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the MeTube base URL as reachable from the Jellyfin server,
    /// for example <c>http://192.168.1.10:8081</c>. Keep MeTube on the LAN:
    /// it has no authentication of its own.
    /// </summary>
    public string MeTubeUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the MeTube download folder (relative to MeTube's download
    /// directory) that is mounted as the Jellyfin library.
    /// </summary>
    public string Folder { get; set; } = "youtube";

    /// <summary>Gets or sets the MeTube presets applied to every download.</summary>
    public string[] Presets { get; set; } = new[] { "jellyfin" };

    /// <summary>Gets or sets the MeTube quality (best, 2160, 1440, 1080, 720, 480, 360, 240, worst).</summary>
    public string Quality { get; set; } = "best";

    /// <summary>Gets or sets the MeTube video format (any, mp4, ios).</summary>
    public string Format { get; set; } = "any";

    /// <summary>Gets or sets the MeTube video codec preference (auto, h264, h265, av1, vp9).</summary>
    public string Codec { get; set; } = "auto";

    /// <summary>
    /// Gets or sets the item id of the Jellyfin library that shows MeTube's
    /// folder. Used to find downloaded files, refresh the library and check
    /// whether a video is already there.
    /// </summary>
    public string LibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the users who may search and add videos. Administrators
    /// are always allowed.
    /// </summary>
    public Guid[] AllowedUserIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets the longest video a non-admin user may add, in minutes.
    /// Zero means no limit.
    /// </summary>
    public int MaxDurationMinutes { get; set; } = 240;

    /// <summary>
    /// Gets or sets how many videos a non-admin user may add per 24 hours.
    /// Zero means no limit.
    /// </summary>
    public int DailyLimitPerUser { get; set; } = 25;

    /// <summary>
    /// Gets or sets the YouTube region code used for search ranking, for
    /// example NL or US. Empty lets YouTube pick from the server's address.
    /// </summary>
    public string SearchRegion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the language for YouTube search result text such as
    /// "3 days ago". English keeps relative dates predictable.
    /// </summary>
    public string SearchLanguage { get; set; } = "en";

    /// <summary>
    /// Gets or sets the YouTube web client version sent with search requests.
    /// Only change this if searches start failing after a YouTube update.
    /// </summary>
    public string InnerTubeClientVersion { get; set; } = "2.20261009.01.00";

    /// <summary>
    /// Gets or sets a value indicating whether the YouTube menu entry is
    /// injected into the web client. Turning this off removes the entry
    /// without uninstalling the plugin; admins can still open the search from
    /// the plugin settings page.
    /// </summary>
    public bool InjectionEnabled { get; set; } = true;
}
