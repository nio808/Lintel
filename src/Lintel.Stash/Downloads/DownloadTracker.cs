// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;

namespace Lintel.Stash;

public sealed record ActiveDownload(string Path, string Name, long Bytes, double BytesPerSecond, DateTimeOffset Started);

public sealed record FinishedDownload(string Path, string Name, long Bytes, DateTimeOffset Finished);

/// <summary>
/// Pure state machine behind the Downloads module; the caller feeds it file events with an explicit clock.
/// Start = a temp file appears or grows. Speed = 5 s moving average over size samples. Completion = the temp file is gone and a
/// non-temp file appeared within 3 s of that (either order; a rename gives both at once). Temp gone with no new file = cancelled.
/// </summary>
public sealed class DownloadTracker
{
    public static readonly string[] TempExtensions = [".crdownload", ".part", ".partial", ".download", ".opdownload"];
    public static readonly TimeSpan CompletionWindow = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(5);
    public const int HistorySize = 10;

    private sealed class Entry(string path, DateTimeOffset started)
    {
        public string Path { get; } = path;
        public DateTimeOffset Started { get; } = started;
        public long Bytes;
        public readonly List<(DateTimeOffset At, long Bytes)> Samples = [];
        public DateTimeOffset? GoneAt;
    }

    private readonly Dictionary<string, Entry> active = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Path, long Bytes, DateTimeOffset At)> recentFiles = [];
    private readonly List<FinishedDownload> history = [];

    public event EventHandler? Changed;

    public static bool IsTempFile(string path) => TempExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>"movie.mkv.crdownload" gives "movie.mkv".</summary>
    public static string DisplayName(string tempPath)
    {
        var name = System.IO.Path.GetFileName(tempPath);
        return IsTempFile(tempPath) ? name[..^System.IO.Path.GetExtension(name).Length] : name;
    }

    /// <summary>Downloads still receiving data, oldest first.</summary>
    public IReadOnlyList<ActiveDownload> Active => active.Values.Where(e => e.GoneAt is null).OrderBy(e => e.Started)
        .Select(e => new ActiveDownload(e.Path, DisplayName(e.Path), e.Bytes, Speed(e), e.Started)).ToList();

    /// <summary>Last 10 finished, newest first.</summary>
    public IReadOnlyList<FinishedDownload> History => history;

    /// <summary>True while something needs a poll or a <see cref="Tick"/> (active download or an undecided disappearance).</summary>
    public bool NeedsPolling => active.Count > 0;

    /// <summary>Paths of downloads that should still be size-polled.</summary>
    public IReadOnlyList<string> PollPaths => active.Values.Where(e => e.GoneAt is null).Select(e => e.Path).ToList();

    /// <summary>A temp file exists with this size (created, changed or polled).</summary>
    public void TempSeen(string path, long size, DateTimeOffset now)
    {
        if (!active.TryGetValue(path, out var e))
            active[path] = e = new Entry(path, now);
        e.GoneAt = null;
        e.Bytes = size;
        e.Samples.Add((now, size));
        e.Samples.RemoveAll(s => now - s.At > SpeedWindow);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A temp file was renamed to another temp name (Chrome: "Unconfirmed 123.crdownload" to "name.crdownload"): same download.</summary>
    public void TempRenamed(string oldPath, string newPath, long size, DateTimeOffset now)
    {
        if (active.Remove(oldPath, out var e))
        {
            var moved = new Entry(newPath, e.Started) { Bytes = e.Bytes };
            moved.Samples.AddRange(e.Samples);
            active[newPath] = moved;
        }
        TempSeen(newPath, size, now);
    }

    /// <summary>The temp file vanished (deleted or renamed away). Completion is decided by <see cref="FileAppeared"/> / <see cref="Tick"/>.</summary>
    public IReadOnlyList<FinishedDownload> TempGone(string path, DateTimeOffset now)
    {
        if (active.TryGetValue(path, out var e) && e.GoneAt is null)
            e.GoneAt = now;
        return Resolve(now);
    }

    /// <summary>A non-temp file appeared (created or renamed into place).</summary>
    public IReadOnlyList<FinishedDownload> FileAppeared(string path, long size, DateTimeOffset now)
    {
        if (IsTempFile(path))
            return [];
        recentFiles.Add((path, size, now));
        return Resolve(now);
    }

    /// <summary>Call about once a second while <see cref="NeedsPolling"/>; expires undecided disappearances as cancelled.</summary>
    public IReadOnlyList<FinishedDownload> Tick(DateTimeOffset now) => Resolve(now);

    private IReadOnlyList<FinishedDownload> Resolve(DateTimeOffset now)
    {
        var done = new List<FinishedDownload>();
        var changed = false;
        recentFiles.RemoveAll(f => now - f.At > CompletionWindow * 2);
        foreach (var e in active.Values.Where(e => e.GoneAt is not null).OrderBy(e => e.GoneAt).ToList())
        {
            var gone = e.GoneAt!.Value;
            var stem = System.IO.Path.GetFileNameWithoutExtension(e.Path);
            var candidates = recentFiles.Where(f => (f.At - gone).Duration() <= CompletionWindow).ToList();
            // Prefer the file whose name is the temp name without its extension; otherwise the closest in time.
            var match = candidates.Cast<(string Path, long Bytes, DateTimeOffset At)?>()
                .FirstOrDefault(f => string.Equals(System.IO.Path.GetFileName(f!.Value.Path), stem, StringComparison.OrdinalIgnoreCase))
                ?? candidates.OrderBy(f => (f.At - gone).Duration()).Cast<(string Path, long Bytes, DateTimeOffset At)?>().FirstOrDefault();
            if (match is { } m)
            {
                recentFiles.Remove(m);
                active.Remove(e.Path);
                var fin = new FinishedDownload(m.Path, System.IO.Path.GetFileName(m.Path), m.Bytes > 0 ? m.Bytes : e.Bytes, now);
                history.Insert(0, fin);
                done.Add(fin);
                changed = true;
            }
            else if (now - gone > CompletionWindow)
            {
                active.Remove(e.Path);   // cancelled
                changed = true;
            }
        }
        if (history.Count > HistorySize)
            history.RemoveRange(HistorySize, history.Count - HistorySize);
        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
        return done;
    }

    /// <summary>Demo/preview only.</summary>
    internal void SeedHistory(FinishedDownload item) => history.Add(item);

    private static double Speed(Entry e)
    {
        if (e.Samples.Count < 2)
            return 0;
        var (t0, b0) = e.Samples[0];
        var (t1, b1) = e.Samples[^1];
        var seconds = (t1 - t0).TotalSeconds;
        return seconds <= 0 ? 0 : Math.Max(0, (b1 - b0) / seconds);
    }
}
