using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.MeTube.Tests;

/// <summary>
/// Release hygiene: the files that name the plugin and its version must agree, and
/// the embedded web files must survive how jellyfin-web loads them.
/// </summary>
public class PluginPlumbingTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void GuidIsTheSameEverywhere()
    {
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "Jellyfin.Plugin.MeTube", "meta.json")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "manifest.json")));

        Assert.Equal(Plugin.PluginId, meta.RootElement.GetProperty("guid").GetString());
        Assert.Equal(Plugin.PluginId, manifest.RootElement[0].GetProperty("guid").GetString());
    }

    [Fact]
    public void VersionIsTheSameInMetaAndCsproj()
    {
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "Jellyfin.Plugin.MeTube", "meta.json")));
        var csproj = File.ReadAllText(Path.Combine(RepoRoot, "Jellyfin.Plugin.MeTube", "Jellyfin.Plugin.MeTube.csproj"));
        var version = meta.RootElement.GetProperty("version").GetString();

        foreach (var property in new[] { "Version", "AssemblyVersion", "FileVersion" })
        {
            Assert.Contains("<" + property + ">" + version + "</" + property + ">", csproj, StringComparison.Ordinal);
        }

        Assert.Contains("var VERSION = '" + version + "';", ReadResource("metube.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void TargetsJellyfin12()
    {
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "Jellyfin.Plugin.MeTube", "meta.json")));
        Assert.Equal("net10.0", meta.RootElement.GetProperty("framework").GetString());
        Assert.StartsWith("12.", meta.RootElement.GetProperty("targetAbi").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigPageSurvivesJellyfinWebsTranslation()
    {
        // jellyfin-web runs plugin pages through translateHtml, which replaces every
        // "${...}" token; a JS template literal in the inline script would be mangled.
        var page = ReadResource("configPage.html");
        Assert.DoesNotContain("${", page, StringComparison.Ordinal);

        // Only the data-role="page" element is kept, so styles must live inside it.
        var pageStart = page.IndexOf("data-role=\"page\"", StringComparison.Ordinal);
        Assert.True(pageStart > 0);
        Assert.True(page.IndexOf("<style>", StringComparison.Ordinal) > pageStart);
        Assert.Contains(Plugin.PluginId, page, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientScriptIsPlainEs5()
    {
        // Older TV browsers run jellyfin-web too.
        var script = ReadResource("metube.js");
        var code = Regex.Replace(script, @"//[^\n]*|/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        Assert.DoesNotContain("=>", code, StringComparison.Ordinal);
        Assert.DoesNotContain("`", code, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\b(let|const|class)\s", code);
    }

    private static string ReadResource(string fileName)
    {
        var assembly = typeof(Plugin).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".Web." + fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "manifest.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
