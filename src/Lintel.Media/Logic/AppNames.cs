// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;

namespace Lintel.Media.Logic;

/// <summary>A WASAPI session reduced to plain data so grouping/naming is testable without COM.</summary>
public sealed record RawSession(int Pid, string? ExePath, string DisplayName, string? FileDescription,
    float Volume, bool Muted, float Peak, bool IsActive, bool IsSystemSounds, bool IsExpired);

/// <summary>One mixer row: every session of one app.</summary>
public sealed record AppRow(string Key, string Name, string? ExePath, IReadOnlyList<int> Pids,
    float Volume, bool Muted, float Peak, bool IsActive, System.Windows.Media.ImageSource? Icon = null);

public static class AppNames
{
    /// <summary>Session display name (ignoring "@resource" indirections), then exe file description, then exe name, then "Unknown app".</summary>
    public static string Resolve(string? displayName, string? fileDescription, string? exePath)
    {
        foreach (var candidate in new[] { displayName, fileDescription })
            if (!string.IsNullOrWhiteSpace(candidate) && !candidate.TrimStart().StartsWith('@'))
                return candidate.Trim();
        return exePath is { Length: > 0 } ? Pretty(Path.GetFileNameWithoutExtension(exePath)) : "Unknown app";
    }

    /// <summary>Source name from an AppUserModelId when Windows has no AppInfo for it: Spotify.exe gives Spotify, Pkg_hash!Main gives Main.</summary>
    public static string FromAumid(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
            return "Unknown app";
        var name = aumid.Contains('!') ? aumid[(aumid.LastIndexOf('!') + 1)..] : aumid;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return Pretty(name);
    }

    /// <summary>Does an SMTC AppUserModelId belong to this executable? (chrome.exe or Chrome vs chrome.exe; Pkg_hash!Spotify vs Spotify.exe.)</summary>
    public static bool AumidMatchesExe(string? aumid, string? exeName)
    {
        if (string.IsNullOrWhiteSpace(aumid) || string.IsNullOrWhiteSpace(exeName))
            return false;
        var exe = Norm(Path.GetFileNameWithoutExtension(exeName));
        if (exe.Length == 0)
            return false;
        var candidates = new List<string> { aumid };
        if (aumid.Contains('!'))
            candidates.Add(aumid[(aumid.LastIndexOf('!') + 1)..]);
        if (aumid.Contains('_'))
            candidates.Add(aumid[..aumid.IndexOf('_')]);
        if (aumid.Contains('.'))
            candidates.Add(aumid[(aumid.LastIndexOf('.') + 1)..]);
        return candidates.Any(c => Norm(c.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? c[..^4] : c) == exe);
    }

    private static string Norm(string s) => new([.. s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    private static string Pretty(string s) => s.Length == 0 ? "Unknown app" : char.ToUpperInvariant(s[0]) + s[1..];
}

public static class AudioGrouping
{
    /// <summary>Drop system sounds / expired / pid 0, group by executable (browsers run many pids), active first, then by name.</summary>
    public static List<AppRow> Group(IEnumerable<RawSession> sessions)
    {
        var rows = new List<AppRow>();
        foreach (var group in sessions.Where(s => !s.IsSystemSounds && !s.IsExpired && s.Pid != 0)
                     .GroupBy(s => string.IsNullOrEmpty(s.ExePath) ? "pid:" + s.Pid : s.ExePath.ToLowerInvariant()))
        {
            var first = group.First();
            var named = group.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.DisplayName) && !s.DisplayName.StartsWith('@')) ?? first;
            rows.Add(new AppRow(group.Key, AppNames.Resolve(named.DisplayName, first.FileDescription, first.ExePath), first.ExePath,
                [.. group.Select(s => s.Pid).Distinct()], group.Max(s => s.Volume), group.All(s => s.Muted),
                group.Max(s => s.Peak), group.Any(s => s.IsActive)));
        }
        return [.. rows.OrderByDescending(r => r.IsActive).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)];
    }
}
