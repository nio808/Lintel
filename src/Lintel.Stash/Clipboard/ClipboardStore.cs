// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Stash;

public sealed record ClipboardLimits
{
    public int MaxEntries { get; init; } = 200;                 // unpinned entries kept
    public int MaxTextBytes { get; init; } = 100 * 1024;
    public int MaxImageBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxImages { get; init; } = 20;
    public TimeSpan Expiry { get; init; } = TimeSpan.FromDays(30);  // unpinned only
}

/// <summary>Pure history model: dedupe/move-to-top, pin, caps, expiry, search. Not thread-safe (UI thread).</summary>
public sealed class ClipboardStore(TimeProvider time, ClipboardLimits? initialLimits = null)
{
    private readonly List<ClipEntry> entries = [];   // newest first
    private ClipboardLimits limits = initialLimits ?? new ClipboardLimits();

    public event EventHandler? Changed;

    public ClipboardLimits Limits
    {
        get => limits;
        set { limits = value; Trim(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>Pinned first (newest first), then the rest newest first.</summary>
    public IReadOnlyList<ClipEntry> Items => entries.Where(e => e.Pinned).Concat(entries.Where(e => !e.Pinned)).ToList();

    public int Count => entries.Count;

    /// <summary>Adds (or moves to top) an entry. Null when it exceeds a size cap or is empty.</summary>
    public ClipEntry? Add(ClipEntry candidate)
    {
        if (!Accepts(candidate))
            return null;
        var key = candidate.Key;
        var existing = entries.FirstOrDefault(e => e.Key == key);
        var entry = existing ?? candidate;
        if (existing is not null)
            entries.Remove(existing);
        entry.Created = time.GetUtcNow();
        entries.Insert(0, entry);
        Trim();
        Changed?.Invoke(this, EventArgs.Empty);
        return entry;
    }

    /// <summary>Merges persisted entries as-is (keeps timestamps and pins).</summary>
    public void Load(IEnumerable<ClipEntry> loaded)
    {
        foreach (var e in loaded.OrderByDescending(e => e.Created))
            if (Accepts(e) && entries.All(x => x.Key != e.Key))
                entries.Add(e);
        entries.Sort((a, b) => b.Created.CompareTo(a.Created));
        Trim();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Pin(Guid id, bool pinned)
    {
        var e = entries.FirstOrDefault(x => x.Id == id);
        if (e is null || e.Pinned == pinned)
            return false;
        e.Pinned = pinned;
        Trim();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Remove(Guid id)
    {
        var removed = entries.RemoveAll(e => e.Id == id) > 0;
        if (removed)
            Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    public void Clear(bool keepPinned = true)
    {
        if (entries.RemoveAll(e => !keepPinned || !e.Pinned) > 0)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops unpinned entries older than the expiry. True if anything went.</summary>
    public bool Expire()
    {
        var cutoff = time.GetUtcNow() - limits.Expiry;
        var removed = entries.RemoveAll(e => !e.Pinned && e.Created < cutoff) > 0;
        if (removed)
            Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>Case-insensitive match over text and file paths; every whitespace-separated term must match.</summary>
    public IReadOnlyList<ClipEntry> Search(string? query)
    {
        var terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
            return Items;
        return Items.Where(e =>
        {
            var hay = e.Kind switch { ClipKind.Text => e.Text ?? "", ClipKind.Files => string.Join('\n', e.Files), _ => "image" };
            return terms.All(t => hay.Contains(t, StringComparison.CurrentCultureIgnoreCase));
        }).ToList();
    }

    private bool Accepts(ClipEntry e) => e.Kind switch
    {
        ClipKind.Text => !string.IsNullOrWhiteSpace(e.Text) && e.Size <= limits.MaxTextBytes,
        ClipKind.Files => e.Files.Count > 0,
        _ => e.Png is { Length: > 0 } && e.Png.Length <= limits.MaxImageBytes,
    };

    private void Trim()
    {
        // Unpinned count cap (pinned entries are exempt), then the image cap; oldest go first.
        foreach (var old in entries.Where(e => !e.Pinned).Skip(limits.MaxEntries).ToList())
            entries.Remove(old);
        foreach (var old in entries.Where(e => e.Kind == ClipKind.Image && !e.Pinned).Skip(limits.MaxImages).ToList())
            entries.Remove(old);
    }
}
