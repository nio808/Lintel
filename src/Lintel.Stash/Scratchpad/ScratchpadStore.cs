// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text;
using System.Text.Json;

namespace Lintel.Stash;

public sealed record ScratchTab(string Id, string Title);

/// <summary>
/// Tabs + notes on disk: data\scratchpad\&lt;id&gt;.txt (UTF-8) and tabs.json. Text edits are written 800 ms after the last
/// keystroke (<see cref="Debouncer"/>); <see cref="Flush"/> writes immediately. Pass dir = null for demo (memory only).
/// </summary>
public sealed class ScratchpadStore : IDisposable
{
    public const int MaxTabs = 8;

    private readonly string? dir;
    private readonly Debouncer debouncer;
    private readonly object gate = new();
    private readonly List<ScratchTab> tabs = [];
    private readonly Dictionary<string, string> texts = [];          // cache of known text
    private readonly HashSet<string> dirty = [];
    private bool tabsDirty;

    public ScratchpadStore(string? dir, TimeProvider time, TimeSpan? delay = null)
    {
        this.dir = dir;
        debouncer = new Debouncer(delay ?? TimeSpan.FromMilliseconds(800), Flush, time);
    }

    public IReadOnlyList<ScratchTab> Tabs
    {
        get { lock (gate) return tabs.ToList(); }
    }

    public void Load()
    {
        lock (gate)
        {
            tabs.Clear();
            if (dir is not null)
            {
                var file = Path.Combine(dir, "tabs.json");
                try
                {
                    if (File.Exists(file))
                        tabs.AddRange((JsonSerializer.Deserialize<List<ScratchTab>>(File.ReadAllText(file)) ?? [])
                            .Where(t => IsSafeId(t.Id)).Take(MaxTabs));
                }
                catch (Exception e) when (e is JsonException or IOException)
                {
                    // unreadable index: fall through to a fresh tab; the .txt files stay on disk
                }
            }
            if (tabs.Count == 0)
                tabs.Add(new ScratchTab(NewId(), NextTitle(tabs)));
        }
    }

    public string GetText(string id)
    {
        lock (gate)
        {
            if (texts.TryGetValue(id, out var cached))
                return cached;
            var text = "";
            if (dir is not null && IsSafeId(id))
            {
                try { text = File.ReadAllText(Path.Combine(dir, id + ".txt"), Encoding.UTF8); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            return texts[id] = text;
        }
    }

    public void SetText(string id, string text)
    {
        lock (gate)
        {
            if (texts.TryGetValue(id, out var old) && old == text)
                return;
            texts[id] = text;
            dirty.Add(id);
        }
        debouncer.Trigger();
    }

    /// <summary>Null when already at <see cref="MaxTabs"/>.</summary>
    public ScratchTab? AddTab(string? text = null)
    {
        ScratchTab tab;
        lock (gate)
        {
            if (tabs.Count >= MaxTabs)
                return null;
            tab = new ScratchTab(NewId(), NextTitle(tabs));
            tabs.Add(tab);
            texts[tab.Id] = text ?? "";
            dirty.Add(tab.Id);
            tabsDirty = true;
        }
        debouncer.Trigger();
        return tab;
    }

    /// <summary>Closes a tab and deletes its file. Returns the id to select next (the neighbour, or a fresh tab when it was the last).</summary>
    public string? RemoveTab(string id)
    {
        string next;
        lock (gate)
        {
            var index = tabs.FindIndex(t => t.Id == id);
            if (index < 0)
                return null;
            tabs.RemoveAt(index);
            texts.Remove(id);
            dirty.Remove(id);
            if (dir is not null && IsSafeId(id))
            {
                try { File.Delete(Path.Combine(dir, id + ".txt")); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            if (tabs.Count == 0)
                tabs.Add(new ScratchTab(NewId(), NextTitle(tabs)));
            next = tabs[Math.Min(index, tabs.Count - 1)].Id;
            tabsDirty = true;
        }
        debouncer.Trigger();
        return next;
    }

    public void Rename(string id, string title)
    {
        title = title.Trim();
        if (title.Length == 0)
            return;
        lock (gate)
        {
            var index = tabs.FindIndex(t => t.Id == id);
            if (index < 0 || tabs[index].Title == title)
                return;
            tabs[index] = tabs[index] with { Title = title.Length > 40 ? title[..40] : title };
            tabsDirty = true;
        }
        debouncer.Trigger();
    }

    /// <summary>Writes everything pending now (also the debounce target).</summary>
    public void Flush()
    {
        lock (gate)
        {
            debouncer.Cancel();
            if (dir is null)
            {
                dirty.Clear();
                tabsDirty = false;
                return;
            }
            try
            {
                Directory.CreateDirectory(dir);
                foreach (var id in dirty.ToList())
                {
                    if (IsSafeId(id))
                        File.WriteAllText(Path.Combine(dir, id + ".txt"), texts.GetValueOrDefault(id, ""), new UTF8Encoding(false));
                    dirty.Remove(id);
                }
                if (tabsDirty)
                {
                    File.WriteAllText(Path.Combine(dir, "tabs.json"), JsonSerializer.Serialize(tabs));
                    tabsDirty = false;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // keep the dirty flags; the next edit retries
            }
        }
    }

    public void Dispose()
    {
        Flush();
        debouncer.Dispose();
    }

    /// <summary>Lowest unused "Note N".</summary>
    internal static string NextTitle(IEnumerable<ScratchTab> existing)
    {
        var used = existing.Select(t => t.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 1; ; n++)
            if (!used.Contains("Note " + n))
                return "Note " + n;
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..8];

    private static bool IsSafeId(string id) => id.Length is > 0 and <= 32 && id.All(char.IsAsciiLetterOrDigit);
}
