// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Lintel.Agents.Logs;
using Lintel.Agents.Pricing;
using Lintel.Core.Agents;

namespace Lintel.Agents.Limits;

internal static class CodexLocator
{
    /// <summary>OpenAI desktop install, ~\.local\bin, PATH, then the native binary inside npm's global package (the .cmd shim cannot start without a shell).</summary>
    public static string? Find()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>
        {
            Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe"),
            Path.Combine(user, ".local", "bin", "codex.exe"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try { candidates.Add(Path.Combine(dir.Trim('"'), "codex.exe")); }
            catch (ArgumentException) { }
        }
        foreach (var root in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex"),
            Path.Combine(local, "Programs", "nodejs", "node_modules", "@openai", "codex"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node_modules", "@openai", "codex"),
        })
        {
            try { if (Directory.Exists(root)) candidates.AddRange(Directory.GetFiles(root, "codex.exe", SearchOption.AllDirectories)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}

/// <summary>
/// Codex plan limits: a hidden <c>codex app-server --listen stdio://</c> speaking JSON-RPC (initialize, initialized,
/// account/rateLimits/read; 20 s per call). The whole process tree is killed in a finally block, after every check and on Stop.
/// CODEX_HOME is set per account. Stdout/stderr contents are never logged.
/// </summary>
internal sealed class CodexAppServerClient(Func<PriceList> prices, TimeProvider time, string? binaryOverride = null) : IDisposable
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);
    private readonly object gate = new();
    private readonly HashSet<Process> running = [];
    private bool disposed;

    public async Task<AgentLimits> ReadAsync(AgentAccount account, CancellationToken ct)
    {
        var binary = binaryOverride ?? CodexLocator.Find()
            ?? throw new LimitReadException("Codex CLI not found. Install Codex and run `codex login`.");
        var info = new ProcessStartInfo(binary)
        {
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("app-server");
        info.ArgumentList.Add("--listen");
        info.ArgumentList.Add("stdio://");
        if (account.HomePath is { Length: > 0 } home) info.Environment["CODEX_HOME"] = home;

        using var process = new Process { StartInfo = info };
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                process.Start();
                running.Add(process);
            }
            process.ErrorDataReceived += (_, _) => { };   // drained, never read
            process.BeginErrorReadLine();
            await Rpc(process, 1, "initialize", new { clientInfo = new { name = "lintel", title = "Lintel", version = "1.0.0" } }, ct).ConfigureAwait(false);
            try { await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}").ConfigureAwait(false); }
            catch (IOException) { throw new LimitReadException("Codex app-server stopped. Check `codex login` and try again."); }
            var result = await Rpc(process, 2, "account/rateLimits/read", null, ct).ConfigureAwait(false);
            return CodexUsageParser.Parse(result, account.Id, prices(), time.GetUtcNow());
        }
        finally
        {
            lock (gate)
            {
                running.Remove(process);
                Kill(process);
            }
        }
    }

    /// <summary>Returns the raw "result" object of the response with this id.</summary>
    private static async Task<string> Rpc(Process process, int id, string method, object? parameters, CancellationToken ct)
    {
        var request = JsonSerializer.Serialize(new { id, method, @params = parameters });
        try
        {
            await process.StandardInput.WriteLineAsync(request).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            throw new LimitReadException("Codex app-server stopped. Check `codex login` and try again.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);
        while (true)
        {
            string? line;
            try { line = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LimitReadException("Codex usage check timed out. Your last reading is kept.");
            }
            if (line is null) throw new LimitReadException("Codex app-server stopped. Check `codex login` and try again.");
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                JsonElement? root = doc.RootElement;
                if (root.Prop("id") is not { } idEl || idEl.ValueKind != JsonValueKind.Number || idEl.GetInt32() != id) continue;
                if (root.Prop("error") is not null)
                    throw new LimitReadException("Codex could not read plan usage. Check `codex login`; API-key logins do not expose plan quotas.");
                return root.Prop("result") is { } result ? result.GetRawText() : "{}";
            }
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Kills any child still running (Stop / app exit).</summary>
    public void KillAll()
    {
        lock (gate)
            foreach (var p in running) Kill(p);
    }

    public void Dispose()
    {
        lock (gate) disposed = true;
        KillAll();
    }
}
