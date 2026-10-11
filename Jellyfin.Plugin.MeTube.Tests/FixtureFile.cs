using System;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.MeTube.Tests;

/// <summary>Reads captured responses from the Fixtures folder.</summary>
internal static class FixtureFile
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static JsonDocument Json(string name) => JsonDocument.Parse(Read(name));
}
