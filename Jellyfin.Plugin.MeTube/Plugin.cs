using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MeTube.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MeTube;

/// <summary>
/// MeTube plugin. Lets selected Jellyfin users search YouTube and queue videos on a
/// MeTube server that downloads into a Jellyfin library.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin id, shared with meta.json and manifest.json.</summary>
    public const string PluginId = "d152b2a6-c648-4b8e-acfd-4a262eb10a4d";

    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the current configuration, or defaults while the plugin is not loaded
    /// (unit tests).
    /// </summary>
    public static PluginConfiguration Config => Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public override string Name => "MeTube";

    /// <inheritdoc />
    public override string Description =>
        "Search YouTube inside Jellyfin and add videos to your library through MeTube.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginId);

    /// <summary>
    /// Reports the version the way meta.json and the plugin catalog write it
    /// (MAJOR.MINOR.PATCH). Jellyfin takes the version it lists for a running plugin
    /// from the assembly, which always has four parts (0.3.2.0), but it looks the
    /// plugin's image up by version against meta.json's 0.3.2. Those two never match,
    /// so the logo vanished from the plugin pages after every restart.
    /// </summary>
    /// <returns>The plugin info with a three-part version.</returns>
    public override PluginInfo GetPluginInfo()
    {
        var info = base.GetPluginInfo();
        info.Version = ManifestVersion(info.Version);
        return info;
    }

    /// <summary>Drops a zero fourth part from a version: 0.3.2.0 becomes 0.3.2.</summary>
    /// <param name="version">The assembly version.</param>
    /// <returns>The version as meta.json writes it.</returns>
    public static Version ManifestVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Revision == 0 ? new Version(version.Major, version.Minor, version.Build) : version;
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = "MeTube",
            DisplayName = "MeTube",
            EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html",
            EnableInMainMenu = true,
            MenuIcon = "ondemand_video"
        }
    };
}
