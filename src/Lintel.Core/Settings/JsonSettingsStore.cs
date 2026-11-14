// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Lintel.Core.Settings;

/// <summary>
/// settings.json = { "schema": 1, "values": { "&lt;key&gt;": &lt;json&gt; } }. Thread-safe.
/// Values use System.Text.Json web defaults (camelCase properties) with strict numbers.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore, IDisposable
{
    public static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.Strict };

    private static readonly JsonSerializerOptions FileOptions = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions ReadOptions =
        new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    private readonly object gate = new();
    private readonly string path;
    private readonly JsonObject values;
    private readonly Timer timer;
    private bool dirty;

    public JsonSettingsStore(string path)
    {
        this.path = Path.GetFullPath(path);
        values = Load(this.path);
        timer = new Timer(_ => Flush());
    }

    public event Action<string>? Changed;

    public T Get<T>(string key, T fallback)
    {
        lock (gate)
        {
            if (!values.TryGetPropertyValue(key, out var node) || node is null)
                return fallback;
            try
            {
                var value = node.Deserialize<T>(SerializerOptions);
                return value is null ? fallback : value;
            }
            catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or FormatException)
            {
                return fallback;
            }
        }
    }

    public void Set<T>(string key, T value)
    {
        if (value is null)
        {
            Remove(key);
            return;
        }
        var node = JsonSerializer.SerializeToNode(value, SerializerOptions);
        lock (gate)
        {
            if (values.TryGetPropertyValue(key, out var old) && JsonNode.DeepEquals(old, node))
                return;
            values[key] = node;
            ScheduleSave();
        }
        Changed?.Invoke(key);
    }

    public bool Contains(string key)
    {
        lock (gate)
            return values.ContainsKey(key);
    }

    public void Remove(string key)
    {
        lock (gate)
        {
            if (!values.Remove(key))
                return;
            ScheduleSave();
        }
        Changed?.Invoke(key);
    }

    public void RemovePrefix(string prefix)
    {
        List<string> removed;
        lock (gate)
        {
            removed = values.Select(p => p.Key).Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var key in removed)
                values.Remove(key);
            if (removed.Count > 0)
                ScheduleSave();
        }
        foreach (var key in removed)
            Changed?.Invoke(key);
    }

    // one lock covers memory and the file write (tiny file, rare writes); add a version counter if disk stalls ever reach the UI thread.
    public void Flush()
    {
        lock (gate)
        {
            timer.Change(Timeout.Infinite, Timeout.Infinite);
            if (!dirty)
                return;
            try
            {
                var root = new JsonObject { ["schema"] = SettingsMigrator.CurrentSchema, ["values"] = values.DeepClone() };
                Write(root.ToJsonString(FileOptions));
                dirty = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                timer.Change(5000, Timeout.Infinite);   // retry; the last Flush on exit may still lose it
            }
        }
    }

    public void Dispose()
    {
        Flush();
        timer.Dispose();
    }

    private void ScheduleSave()
    {
        dirty = true;
        timer.Change(500, Timeout.Infinite);
    }

    private void Write(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        if (File.Exists(path))
            File.Replace(temp, path, null, ignoreMetadataErrors: true);
        else
            File.Move(temp, path);
    }

    private static JsonObject Load(string path)
    {
        if (!File.Exists(path))
            return new JsonObject();
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) as JsonObject
                ?? throw new JsonException("settings root is not an object");
            var migrated = SettingsMigrator.Migrate(root);
            var result = migrated["values"] as JsonObject ?? throw new JsonException("values is not an object");
            migrated.Remove("values");
            return result;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            try
            {
                var corrupt = Path.Combine(Path.GetDirectoryName(path)!, $"settings.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
                File.Move(path, corrupt, overwrite: true);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                // Keep going with empty settings; the next save replaces the unreadable file.
            }
            return new JsonObject();
        }
    }
}
