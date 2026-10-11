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
