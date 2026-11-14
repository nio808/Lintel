// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Lintel.Agents.Accounts;
using Lintel.Core.Agents;

namespace Lintel.Agents.Logs;

internal sealed record LogFile(string Path, AgentProvider Provider, string? AccountId, long Length, DateTime LastWriteUtc);

/// <summary>Where each provider keeps its logs. Pure paths: nothing is touched until <see cref="Discover"/>.</summary>
internal sealed class LogRoots
{
    private static readonly EnumerationOptions Recursive = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
    private static readonly EnumerationOptions Flat = new() { IgnoreInaccessible = true };

    public string? ClaudeConfigDir { get; init; }

    /// <summary>(account id, CODEX_HOME folder) per Codex account.</summary>
    public IReadOnlyList<(string AccountId, string Home)> CodexHomes { get; init; } = [];

    public string? CopilotSessionState { get; init; }
    public string? OpenCodeDb { get; init; }

    /// <summary>CLAUDE_CONFIG_DIR | ~\.claude, CODEX_HOME | ~\.codex (+ added homes), ~\.copilot, ~\.local\share\opencode.</summary>
    public static LogRoots FromEnvironment(IEnumerable<(string AccountId, string Home)> extraCodexHomes)
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : "";
        var data = Env("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(user, ".local", "share");
        return new()
        {
            ClaudeConfigDir = Env("CLAUDE_CONFIG_DIR") is { Length: > 0 } c ? c : Path.Combine(user, ".claude"),
            CodexHomes = [(CodexAccounts.DefaultId, DefaultCodexHome()), .. extraCodexHomes],
            CopilotSessionState = Path.Combine(user, ".copilot", "session-state"),
            OpenCodeDb = Path.Combine(data, "opencode", "opencode.db"),
        };
    }

    public static string DefaultCodexHome() =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h
            ? h : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    /// <summary>Log files modified since <paramref name="horizon"/>, oldest first so live turns settle on the newest.</summary>
    public List<LogFile> Discover(DateTime horizonUtc, Func<AgentProvider, bool> enabled)
    {
        var found = new List<LogFile>();
        if (enabled(AgentProvider.Claude) && ClaudeConfigDir is not null)
            Scan(Path.Combine(ClaudeConfigDir, "projects"), "*.jsonl", Recursive, AgentProvider.Claude, null, horizonUtc, found);
        if (enabled(AgentProvider.Codex))
            foreach (var (id, home) in CodexHomes)
            {
                foreach (var dir in new[] { "sessions", "archived_sessions" })
                    foreach (var pattern in new[] { "*.jsonl", "*.jsonl.zst" })
                        Scan(Path.Combine(home, dir), pattern, Recursive, AgentProvider.Codex, id, horizonUtc, found);
            }
        if (enabled(AgentProvider.Copilot) && CopilotSessionState is not null && Directory.Exists(CopilotSessionState))
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(CopilotSessionState))
                    Scan(dir, "events.jsonl", Flat, AgentProvider.Copilot, null, horizonUtc, found);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        found.Sort((a, b) => a.LastWriteUtc.CompareTo(b.LastWriteUtc));
        return found;
    }

    /// <summary>The OpenCode database when it exists and the provider is on (its mtime includes the WAL).</summary>
    public (string Path, DateTime LastWriteUtc, long Length)? OpenCodeFile()
    {
        if (OpenCodeDb is null) return null;
        try
        {
            var db = new FileInfo(OpenCodeDb);
            if (!db.Exists) return null;
            var wal = new FileInfo(OpenCodeDb + "-wal");
            var time = wal.Exists && wal.LastWriteTimeUtc > db.LastWriteTimeUtc ? wal.LastWriteTimeUtc : db.LastWriteTimeUtc;
            return (db.FullName, time, db.Length + (wal.Exists ? wal.Length : 0));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void Scan(string root, string pattern, EnumerationOptions options, AgentProvider provider, string? account,
                             DateTime horizonUtc, List<LogFile> into)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var file in new DirectoryInfo(root).EnumerateFiles(pattern, options))
            {
                var plain = file.Name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
                var zst = provider == AgentProvider.Codex && file.Name.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase);
                if (!(plain || zst) || file.LastWriteTimeUtc < horizonUtc) continue;
                into.Add(new LogFile(file.FullName, provider, account, file.Length, file.LastWriteTimeUtc));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
