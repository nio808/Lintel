// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;

namespace Lintel.Core.Agents;

/// <summary>
/// The Claude Code CLI owns <c>.credentials.json</c>. Lintel never writes that file: when the access token expires it asks
/// the CLI to renew it (<c>claude auth status</c>, hidden) or, if that is not enough, opens <c>claude auth login</c>.
/// </summary>
public static class ClaudeCli
{
    public const string SignInMessage = "Claude sign-in expired. Use Sign in to renew it.";
    public const string SetupUrl = "https://docs.anthropic.com/en/docs/claude-code/setup";

    /// <summary>~\.local\bin (the native installer), then PATH: claude.exe, or npm's claude.cmd.</summary>
    public static string? Find()
    {
        var candidates = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe") };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                candidates.Add(Path.Combine(dir.Trim('"'), "claude.exe"));
                candidates.Add(Path.Combine(dir.Trim('"'), "claude.cmd"));
            }
            catch (ArgumentException) { }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// <c>claude args</c>. Variables of a Claude Code session Lintel may have been started from are dropped (they point the
    /// CLI at that session's login, not the user's own); CLAUDE_CONFIG_DIR is kept or set.
    /// </summary>
    public static ProcessStartInfo StartInfo(string binary, string? configDir, params string[] args)
    {
        var viaCmd = binary.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo(viaCmd ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : binary)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (viaCmd) foreach (var a in new[] { "/d", "/c", binary }) info.ArgumentList.Add(a);
        foreach (var a in args) info.ArgumentList.Add(a);
        foreach (var key in info.Environment.Keys.ToList())
            if (key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
                || key.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase))
                info.Environment.Remove(key);
        if (configDir is not null) info.Environment["CLAUDE_CONFIG_DIR"] = configDir;
        return info;
    }

    /// <summary>Opens a console running <c>claude auth login</c> and completes when it closes. False when the CLI is not installed.</summary>
    public static async Task<bool> SignInAsync()
    {
        if (Find() is not { } binary) return false;
        try
        {
            using var process = Process.Start(StartInfo(binary, null, "auth", "login"));
            if (process is null) return false;
            await process.WaitForExitAsync().ConfigureAwait(false);
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
