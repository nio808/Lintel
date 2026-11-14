// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Lintel.Core.Agents;
using Lintel.Core.Settings;

namespace Lintel.Agents.Accounts;

/// <summary>Shape of one entry of <c>agents.codex.homes</c>: { "id", "name", "path" }.</summary>
internal sealed record CodexHome(string Id, string Name, string Path);

/// <summary>Setting keys the data service reads.</summary>
internal static class AgentSettings
{
    public static string Provider(AgentProvider p) => "agents.providers." + p.ToString().ToLowerInvariant();
    public const string CodexHomes = "agents.codex.homes";
    public const string AlertsEnabled = "agents.alerts.enabled";
    public const string AlertsThresholds = "agents.alerts.thresholds";
    public const string AlertsOnReset = "agents.alerts.onReset";
    public const string FinishEnabled = "agents.finish.enabled";
    public const string FinishMinSeconds = "agents.finish.minSeconds";
    /// <summary>Owned by the data service: UTC time of the last price-list download attempt.</summary>
    public const string PricesChecked = "agents.prices.checked";
}

/// <summary><c>agents.codex.homes</c> ↔ <see cref="AgentAccount"/>. Ids: "codex:default", "codex:&lt;slug&gt;".</summary>
internal static class CodexAccounts
{
    public const string DefaultId = "codex:default";

    public static List<CodexHome> Load(ISettingsStore settings) =>
        settings.Get(AgentSettings.CodexHomes, new List<CodexHome>()).Where(h => !string.IsNullOrWhiteSpace(h.Id) && !string.IsNullOrWhiteSpace(h.Path)).ToList();

    public static AgentAccount ToAccount(CodexHome h) => new(h.Id, AgentProvider.Codex, h.Name, h.Path, false);

    /// <summary>Lowercase letters and digits, dashes between; unique among <paramref name="taken"/> by numeric suffix.</summary>
    public static string Slug(string name, IEnumerable<string> taken)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        var slug = sb.ToString().Trim('-');
        if (slug.Length == 0) slug = "home";
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var candidate = slug;
        for (var n = 2; used.Contains("codex:" + candidate) || candidate == "default"; n++) candidate = $"{slug}-{n}";
        return "codex:" + candidate;
    }

    /// <summary>Adds (or, for the same folder, renames) an extra Codex home and persists it.</summary>
    public static AgentAccount Add(ISettingsStore settings, string folder, string displayName)
    {
        var homes = Load(settings);
        var existing = homes.FirstOrDefault(h => string.Equals(h.Path, folder, StringComparison.OrdinalIgnoreCase));
        var name = string.IsNullOrWhiteSpace(displayName) ? System.IO.Path.GetFileName(folder.TrimEnd('\\', '/')) : displayName.Trim();
        var home = existing is null
            ? new CodexHome(Slug(name, homes.Select(h => h.Id)), name, folder)
            : existing with { Name = name };
        homes = [.. homes.Where(h => h.Id != home.Id), home];
        settings.Set(AgentSettings.CodexHomes, homes);
        return ToAccount(home);
    }

    public static bool Remove(ISettingsStore settings, string accountId)
    {
        var homes = Load(settings);
        var left = homes.Where(h => !string.Equals(h.Id, accountId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (left.Count == homes.Count) return false;
        settings.Set(AgentSettings.CodexHomes, left);
        return true;
    }
}
