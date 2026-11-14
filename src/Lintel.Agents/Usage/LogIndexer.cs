// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Lintel.Agents.Logs;
using Lintel.Core.Agents;

namespace Lintel.Agents.Usage;

/// <summary>
/// Owns incremental indexing of every provider's logs into the store and the turn tracker. Single-threaded by contract:
/// the service runs it on one worker and guards snapshots with its own lock. No FileSystemWatcher: discovery every 30 s,
/// tail every 2 s only for files in an open turn or modified within 30 minutes.
/// </summary>
internal sealed class LogIndexer(Func<LogRoots> rootsFactory, UsageStore store, TurnTracker turns, TimeProvider time, Func<AgentProvider, bool> enabled,
                                 string? cachePath = null, Action<string>? warn = null, object? gate = null)
{
    private readonly object sync = gate ?? new();
    private LogRoots roots = rootsFactory();

    public static readonly TimeSpan HotWindow = TimeSpan.FromMinutes(30);

    private static readonly Dictionary<AgentProvider, ILogReader> Readers = new ILogReader[]
        { new ClaudeLogReader(), new CodexLogReader(), new CopilotLogReader() }.ToDictionary(r => r.Provider);

    private sealed class Tracked(string path, AgentProvider provider, string? account, FileProgress progress)
    {
        public string Path { get; } = path;
        public AgentProvider Provider { get; } = provider;
        public string? AccountId { get; } = account;
        public FileProgress Progress { get; } = progress;
        public DateTime LastWriteUtc { get; set; }
        public long Length { get; set; }
    }

    private readonly Dictionary<string, Tracked> tracked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileProgress> cached = LoadCache(cachePath, store);
    private readonly Dictionary<string, string> sessionFiles = [];
    private FileProgress? openCode;
    private (DateTime, long) openCodeSeen;
    private bool openCodeWarned;
    private readonly List<LogEntry> scratch = [];

    /// <summary>Raised on the worker for Finished events (the service applies the user's settings).</summary>
    public Action<AgentUsageEvent>? OnEvent { get; set; }

    /// <summary>Latest limit reading copied into a Codex account's session logs, by account id.</summary>
    public Dictionary<string, (IReadOnlyList<AgentLimitWindow> Windows, DateTimeOffset ObservedAt)> CodexLimits { get; } = [];
    public Dictionary<string, (string Name, DateTimeOffset ObservedAt)> CodexPlans { get; } = [];

    /// <summary>Providers that have any log file or database on disk.</summary>
    public IEnumerable<AgentProvider> Present =>
        tracked.Values.Select(t => t.Provider).Concat(openCode is null ? [] : [AgentProvider.OpenCode]).Distinct();

    public int TrackedFiles => tracked.Count;

    private static Dictionary<string, FileProgress> LoadCache(string? path, UsageStore store)
    {
        if (path is null) return new(StringComparer.OrdinalIgnoreCase);
        var seen = new List<SeenEntry>();
        var (files, buckets) = UsageCache.Load(path, seen);
        foreach (var (k, b) in buckets) store.Load(k, b);
        store.LoadSeen(seen);
        return files;
    }

    /// <summary>One indexing pass. <paramref name="discover"/> re-scans the roots; otherwise only hot files are tailed.</summary>
    public bool Pass(bool discover, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var changed = false;
        if (discover) lock (sync) { roots = rootsFactory(); changed |= Discover(now); }
        foreach (var t in tracked.Values.OrderBy(t => t.LastWriteUtc).ToList())
        {
            if (ct.IsCancellationRequested) return changed;
            var hot = t.Progress.State.TurnOpen || now.UtcDateTime - t.LastWriteUtc < HotWindow;
            if (!discover && !hot) continue;
            lock (sync) changed |= Tail(t, now, ct);
        }
        lock (sync)
        {
            if (discover && enabled(AgentProvider.OpenCode)) changed |= ReadOpenCode(now);
            turns.Prune(now);
            changed |= store.Prune(now);
        }
        return changed;
    }

    private bool Discover(DateTimeOffset now)
    {
        var changed = false;
        var found = roots.Discover(now.UtcDateTime - UsageStore.Retention, enabled);
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in found)
        {
            live.Add(f.Path);
            if (!tracked.TryGetValue(f.Path, out var t))
            {
                if (!cached.TryGetValue(f.Path, out var progress) || progress.Provider != f.Provider) progress = new FileProgress { Provider = f.Provider };
                tracked[f.Path] = t = new Tracked(f.Path, f.Provider, f.AccountId, progress);
                if (progress.State.Session.Length > 0 && !progress.State.CodexSubagent) sessionFiles[progress.State.Session] = f.Path;
            }
            t.LastWriteUtc = f.LastWriteUtc;
            t.Length = f.Length;
        }
        foreach (var gone in tracked.Keys.Where(k => !live.Contains(k)).ToList())
        {
            turns.Forget(gone);
            tracked.Remove(gone);
            cached.Remove(gone);
            changed = true;
        }
        return changed;
    }

    private bool Tail(Tracked t, DateTimeOffset now, CancellationToken ct)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(t.Path);
            if (!info.Exists) return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        t.LastWriteUtc = info.LastWriteTimeUtc;
        t.Length = info.Length;
        var p = t.Progress;
        // A compressed log's offset counts decompressed bytes, so "unchanged" means the same size and time on disk.
        var compressed = LogTailer.IsCompressed(t.Path);
        if (compressed ? t.Length == p.Size && info.LastWriteTimeUtc.Ticks == p.Mtime : t.Length == p.Offset)
        {
            p.Mtime = info.LastWriteTimeUtc.Ticks;
            return false;
        }

        // Truncated or replaced: forget the open turn and parser context, read again from the start.
        void Restart()
        {
            turns.Forget(t.Path);
            p.State = new LogState();
            p.Offset = 0;
        }
        if (!compressed && t.Length < p.Offset) Restart();

        var before = p.Offset;
        var reader = Readers[t.Provider];
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        try
        {
            p.Offset = LogTailer.ReadLines(t.Path, p.Offset, line =>
            {
                scratch.Clear();
                try { reader.Parse(line, p.State, now, scratch); }
                catch (Exception e) when (e is not OutOfMemoryException) { return; }   // a damaged line never stops the file
                foreach (var entry in scratch) Apply(entry, t, modified, now);
            }, out _, ct, Restart);
        }
        catch (Exception e) when (compressed && e is not (OutOfMemoryException or UnauthorizedAccessException)
                                  && (e is not IOException || e is EndOfStreamException))
        {
            // Truncated or damaged archive: keep what was counted, say so once, and wait for the file to change.
            warn?.Invoke($"Skipped a damaged compressed log ({Path.GetFileName(t.Path)}).");
            p.Size = t.Length;
            p.Mtime = info.LastWriteTimeUtc.Ticks;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        p.Size = t.Length;
        p.Mtime = info.LastWriteTimeUtc.Ticks;
        if (t.Provider == AgentProvider.Codex && p.State.Session.Length > 0 && !p.State.CodexSubagent) sessionFiles[p.State.Session] = t.Path;
        return p.Offset != before;
    }

    private void Apply(LogEntry entry, Tracked t, DateTimeOffset modified, DateTimeOffset now)
    {
        var state = t.Progress.State;
        var subagentFile = t.Provider == AgentProvider.Claude && IsClaudeSubagentFile(t.Path)
                           || t.Provider == AgentProvider.Codex && state.CodexSubagent;
        var tracksTurns = !subagentFile;
        switch (entry)
        {
            case LogEntry.Usage u:
                var delta = store.Add(u, now);
                if (!delta.Changed || t.Provider == AgentProvider.Copilot) break;
                var turnFile = tracksTurns ? t.Path : ParentTurnFile(t, state);
                if (turnFile is not null) turns.AddUsage(turnFile, u.Date, delta, u.Model, u.Project, subagent: !tracksTurns);
                break;
            case LogEntry.Limits l when t.AccountId is not null:
                if (!CodexLimits.TryGetValue(t.AccountId, out var known) || l.ObservedAt >= known.ObservedAt)
                    CodexLimits[t.AccountId] = (l.Windows, l.ObservedAt);
                break;
            case LogEntry.Plan p when t.AccountId is not null:
                if (!CodexPlans.TryGetValue(t.AccountId, out var plan) || p.ObservedAt >= plan.ObservedAt)
                    CodexPlans[t.AccountId] = (p.Name, p.ObservedAt);
                break;
            case LogEntry.TurnBegan b when tracksTurns:
                turns.Began(t.Path, t.Provider, b.At);
                turns.Context(t.Path, state.Model, state.Project);
                break;
            case LogEntry.TurnActive a when tracksTurns:
                turns.Active(t.Path, t.Provider, a.At, modified);
                turns.Context(t.Path, state.Model, state.Project);
                break;
            case LogEntry.TurnEnded e when tracksTurns:
                if (turns.Ended(t.Path, e.At, e.Completed, e.Duration, modified, now) is { } finished) OnEvent?.Invoke(finished);
                break;
            case LogEntry.Reset:
                turns.Forget(t.Path);
                break;
        }
    }

    /// <summary>Claude's newer layout keeps subagent logs at projects\&lt;proj&gt;\&lt;session&gt;\subagents\agent-*.jsonl.</summary>
    internal static bool IsClaudeSubagentFile(string path) =>
        path.Contains("\\subagents\\", StringComparison.OrdinalIgnoreCase) || path.Contains("/subagents/", StringComparison.OrdinalIgnoreCase);

    private string? ParentTurnFile(Tracked t, LogState state)
    {
        if (t.Provider == AgentProvider.Claude)
        {
            var sessionDir = Path.GetDirectoryName(Path.GetDirectoryName(t.Path));
            return sessionDir is null ? null : sessionDir + ".jsonl";
        }
        return state.CodexRoot.Length > 0 && sessionFiles.TryGetValue(state.CodexRoot, out var file) ? file : null;
    }

    private bool ReadOpenCode(DateTimeOffset now)
    {
        if (roots.OpenCodeFile() is not { } db) return false;
        var (path, mtime, length) = db;
        openCode ??= cached.GetValueOrDefault(path) is { Provider: AgentProvider.OpenCode } c ? c : new FileProgress { Provider = AgentProvider.OpenCode };
        if (openCodeSeen == (mtime, length)) return false;
        var result = OpenCodeDbReader.Read(path, openCode.Offset, now - UsageStore.Retention, now);
        if (!result.Supported)
        {
            if (!openCodeWarned) warn?.Invoke("OpenCode database has an unsupported layout; its usage is not shown.");
            openCodeWarned = true;
            openCodeSeen = (mtime, length);
            return false;
        }
        foreach (var u in result.Records) store.Add(u, now);
        openCode.Offset = result.Rowid;
        openCode.Mtime = mtime.Ticks;
        // A full batch means more rows are waiting: do not mark the file as seen so the next pass continues.
        if (result.Records.Count < OpenCodeDbReader.BatchSize) openCodeSeen = (mtime, length);
        cached[path] = openCode;
        return result.Records.Count > 0;
    }

    /// <summary>Persists offsets, parser state and aggregates (every 5 minutes and on Stop).</summary>
    public void Save()
    {
        if (cachePath is null) return;
        lock (sync) SaveLocked();
    }

    private void SaveLocked()
    {
        var files = tracked.ToDictionary(p => p.Key, p => p.Value.Progress, StringComparer.OrdinalIgnoreCase);
        if (openCode is not null && roots.OpenCodeDb is { } db) files[db] = openCode;
        try { UsageCache.Save(cachePath!, files, store.Buckets, store.ExportSeen()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warn?.Invoke("Could not save the usage cache."); }
    }
}
