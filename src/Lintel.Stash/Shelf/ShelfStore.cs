// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lintel.Stash;

public enum ShelfKind { File, Text, Link }

public sealed record ShelfItem(Guid Id, ShelfKind Kind, string Value, bool IsFolder, DateTimeOffset Added)
{
    /// <summary>File or folder name; for text the first line; for a link the host.</summary>
    [JsonIgnore]
    public string Name
    {
        get
        {
            switch (Kind)
            {
                case ShelfKind.File:
                    var trimmed = Value.TrimEnd('\\', '/');
                    var name = Path.GetFileName(trimmed);
                    return name.Length > 0 ? name : trimmed;
                case ShelfKind.Link:
                    return Uri.TryCreate(Value, UriKind.Absolute, out var u) ? u.Host + (u.AbsolutePath.Length > 1 ? u.AbsolutePath : "") : Value;
                default:
                    var line = Value.Trim().ReplaceLineEndings(" ");
                    return line.Length > 40 ? line[..40] + "…" : line;
            }
        }
    }
}

/// <summary>Shelf list: references only (no copies). Newest first, max 50, duplicates move to the front. UI thread.</summary>
public sealed class ShelfStore(string? path, TimeProvider time, Func<string, bool>? exists = null)
{
    public const int MaxItems = 50;
    public const int MaxTextChars = 100_000;

    private readonly List<ShelfItem> items = [];
    private readonly Func<string, bool> exists = exists ?? (p => File.Exists(p) || Directory.Exists(p));

    public event EventHandler? Changed;

    public IReadOnlyList<ShelfItem> Items => items;

    /// <summary>A file/folder item whose target is gone.</summary>
    public bool IsMissing(ShelfItem item) => item.Kind == ShelfKind.File && !exists(item.Value);

    public ShelfItem? Add(ShelfKind kind, string value, bool isFolder = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxTextChars)
            return null;
        items.RemoveAll(i => i.Kind == kind && string.Equals(i.Value, value, kind == ShelfKind.File ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        var item = new ShelfItem(Guid.NewGuid(), kind, value, isFolder, time.GetUtcNow());
        items.Insert(0, item);
        if (items.Count > MaxItems)
            items.RemoveRange(MaxItems, items.Count - MaxItems);
        Persist();
        return item;
    }

    public void Remove(Guid id)
    {
        if (items.RemoveAll(i => i.Id == id) > 0)
            Persist();
    }

    public void Clear()
    {
        if (items.Count == 0)
            return;
        items.Clear();
        Persist();
    }

    public void Load()
    {
        items.Clear();
        if (path is null || !File.Exists(path))
            return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<ShelfItem>>(File.ReadAllText(path)) ?? [];
            items.AddRange(loaded.Where(i => !string.IsNullOrWhiteSpace(i.Value)).Take(MaxItems));
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            try { File.Move(path, path + ".corrupt", overwrite: true); } catch (IOException) { }
        }
    }

    private void Persist()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        if (path is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items));
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException) { /* next change retries */ }
    }
}
