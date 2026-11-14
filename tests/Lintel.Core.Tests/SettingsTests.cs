// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.Json.Nodes;
using Lintel.Core.Settings;

namespace Lintel.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly TempDir dir = new();

    private sealed record Home(string Id, string Name, string Path);

    public void Dispose() => dir.Dispose();

    [Fact]
    public void RoundTripsValuesThroughTheFile()
    {
        var path = dir.File("settings.json");
        using (var store = new JsonSettingsStore(path))
        {
            store.Set("test.ratio", 1.15);
            store.Set("test.list", new[] { "agents", "music" });
            store.Set("agents.codex.homes", new[] { new Home("codex:work", "Work", @"C:\codex") });
            store.Set("app.onboarded", true);
        }
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(1, (int)json["schema"]!);
        Assert.Equal("Work", (string)json["values"]!["agents.codex.homes"]![0]!["name"]!);

        using var reloaded = new JsonSettingsStore(path);
        Assert.Equal(1.15, reloaded.Get("test.ratio", 1.0));
        Assert.Equal(["agents", "music"], reloaded.Get("test.list", Array.Empty<string>()));
        Assert.Equal(@"C:\codex", reloaded.Get("agents.codex.homes", Array.Empty<Home>())[0].Path);
        Assert.True(reloaded.Get("app.onboarded", false));
    }

    [Fact]
    public void WrongTypeOrMissingReturnsFallback()
    {
        using var store = new JsonSettingsStore(dir.File("settings.json"));
        store.Set("notch.expandOn", "hover");
        Assert.Equal(7, store.Get("notch.expandOn", 7));
        Assert.Equal("x", store.Get("missing", "x"));
        store.Set<string?>("notch.expandOn", null);
        Assert.False(store.Contains("notch.expandOn"));
    }

    [Fact]
    public void SetRaisesChangedOnlyWhenTheValueChanges()
    {
        using var store = new JsonSettingsStore(dir.File("settings.json"));
        var changed = new List<string>();
        store.Changed += changed.Add;
        store.Set("a", 1);
        store.Set("a", 1);
        store.Set("a", 2);
        store.Remove("a");
        store.Remove("a");
        Assert.Equal(["a", "a", "a"], changed);
    }

    [Fact]
    public void SaveIsDebouncedAndFlushWritesNow()
    {
        var path = dir.File("settings.json");
        using var store = new JsonSettingsStore(path);
        store.Set("a", 1);
        Assert.False(File.Exists(path));
        store.Flush();
        Assert.True(File.Exists(path));

        store.Set("b", 2);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ReadWhenFree(path).Contains("\"b\"") && DateTime.UtcNow < deadline)
            Thread.Sleep(50);
        Assert.Contains("\"b\"", ReadWhenFree(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    // The debounced save replaces the file on a timer thread; a read that lands mid-replace gets a sharing violation.
    private static string ReadWhenFree(string path)
    {
        for (var i = 0; ; i++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (i < 40) { Thread.Sleep(25); }
        }
    }

    [Fact]
    public void RemovePrefixRemovesOnlyMatchingKeys()
    {
        using var store = new JsonSettingsStore(dir.File("settings.json"));
        store.Set("clipboard.limit", 200);
        store.Set("clipboard.persist", true);
        store.Set("modules.clipboard.installed", true);
        store.RemovePrefix("clipboard.");
        Assert.False(store.Contains("clipboard.limit"));
        Assert.False(store.Contains("clipboard.persist"));
        Assert.True(store.Contains("modules.clipboard.installed"));
    }

    [Fact]
    public void CorruptFileIsRenamedAndStoreStartsEmpty()
    {
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{ not json");
        using var store = new JsonSettingsStore(path);
        Assert.False(store.Contains("anything"));
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(dir.Path, "settings.corrupt-*.json"));
        store.Set("a", 1);
        store.Flush();
        using var reloaded = new JsonSettingsStore(path);
        Assert.Equal(1, reloaded.Get("a", 0));
    }

    [Fact]
    public void MigratesFlatSchemaZeroIntoValues()
    {
        var flat = JsonNode.Parse("""{ "test.ratio": 1.2, "app.onboarded": true }""")!.AsObject();
        var migrated = SettingsMigrator.Migrate(flat);
        Assert.Equal(1, (int)migrated["schema"]!);
        Assert.Equal(1.2, (double)migrated["values"]!["test.ratio"]!);

        var explicitZero = SettingsMigrator.Migrate(JsonNode.Parse("""{ "schema": 0, "a": 1 }""")!.AsObject());
        Assert.Null(explicitZero["values"]!["schema"]);
        Assert.Equal(1, (int)explicitZero["values"]!["a"]!);

        var current = JsonNode.Parse("""{ "schema": 1, "values": { "a": 2 } }""")!.AsObject();
        Assert.Same(current, SettingsMigrator.Migrate(current));
    }

    [Fact]
    public void LoadsAFlatLegacyFile()
    {
        var path = dir.File("settings.json");
        File.WriteAllText(path, """{ "notch.expandOn": "click" }""");
        using var store = new JsonSettingsStore(path);
        Assert.Equal("click", store.Get("notch.expandOn", "hover"));
    }
}
