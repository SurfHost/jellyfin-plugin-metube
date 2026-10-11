using System;
using Xunit;

namespace Jellyfin.Plugin.MeTube.Tests;

/// <summary>
/// Jellyfin finds a plugin's image by the version it lists, against meta.json's
/// three-part version; the assembly's four-part version must be reported without
/// its zero revision.
/// </summary>
public class PluginVersionTests
{
    [Theory]
    [InlineData("0.3.2.0", "0.3.2")]
    [InlineData("1.0.0.0", "1.0.0")]
    [InlineData("0.3.2.5", "0.3.2.5")]
    [InlineData("0.3.2", "0.3.2")]
    public void ReportsTheVersionAsMetaJsonWritesIt(string assembly, string expected)
    {
        Assert.Equal(Version.Parse(expected), Plugin.ManifestVersion(Version.Parse(assembly)));
    }
}
