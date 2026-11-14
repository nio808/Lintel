// SPDX-License-Identifier: GPL-3.0-or-later
// Portions adapted from vorssaint-utils, Copyright (C) 2026 Vorssaint, GPL-3.0-or-later.
using System.Globalization;
using Lintel.Agents.Logs;
using Lintel.Core.Agents;

namespace Lintel.Agents.Pricing;

/// <summary>
/// Costs are what the same traffic would bill through the API; a plan pays a flat price instead, so the app presents
/// them as "API value". Unknown model: cost null (counted as unpriced), never guessed.
/// </summary>
internal sealed class AgentPricing(PriceList list)
{
    /// <summary>Words that make a variant a model of its own, with its own price.</summary>
    private static readonly string[] Siblings =
        ["mini", "nano", "pro", "lite", "research", "search", "audio", "realtime", "transcribe", "tts", "cyber", "image"];

    public PriceList List { get; } = list;

    /// <summary>Provider prefixes, snapshot dates and context tags vary by where the model runs; the family name does not.</summary>
    public static string Normalize(string model)
    {
        var id = model.ToLowerInvariant().Trim();
        var c = id.IndexOf("claude-", StringComparison.Ordinal);
        if (c >= 0) id = id[c..];
        // Copilot names Claude point releases with a dot where Anthropic's logs and the price list use a dash.
        if (id.StartsWith("claude-", StringComparison.Ordinal)) id = id.Replace('.', '-');
        var slash = id.LastIndexOf('/');
        if (slash >= 0) id = id[(slash + 1)..];
        foreach (var marker in new[] { '@', '[' })
        {
            var i = id.IndexOf(marker);
            if (i >= 0) id = id[..i];
        }
        return id;
    }

    /// <summary>The longest matching family wins, so a point release never inherits the price of the version it extends.</summary>
    public ModelPrice? Price(string model)
    {
        var id = Normalize(model);
        var table = id.StartsWith("claude-", StringComparison.Ordinal) ? List.Claude : List.Codex;
        ModelPrice? match = null;
        foreach (var candidate in table)
            if (Matches(id, candidate.Id) && (match is null || candidate.Id.Length > match.Id.Length)) match = candidate;
        if (match is null) return null;
        // A smaller, larger or specialised sibling the list does not name is priced very differently: no figure beats a wrong one.
        var rest = id[match.Id.Length..];
        return Siblings.Any(w => rest.Contains(w, StringComparison.Ordinal)) ? null : match;
    }

    /// <summary>A family matches at a word boundary: "gpt-5" is not "gpt-5.1"; a number after it names another version; a snapshot date is the same model.</summary>
    private static bool Matches(string id, string family)
    {
        if (!id.StartsWith(family, StringComparison.Ordinal)) return false;
        if (id.Length == family.Length) return true;
        var next = id[family.Length];
        if (next is not ('-' or '_' or ':')) return false;
        var digits = id.Skip(family.Length + 1).TakeWhile(char.IsDigit).Count();
        return digits == 0 || digits >= 4;
    }

    public (double? Cost, double Savings) Cost(Billable b, string model)
    {
        // Activity-only records establish a date, not unpriced token usage.
        if (b.IsAggregate && b.Tokens.Total == 0) return (0, 0);
        if (Price(model) is not { } price) return (null, 0);
        var t = b.Tokens;
        var @long = Math.Min(Math.Max(0, b.LongCacheWrite), t.CacheWrite);
        var multiplier = b.Fast ? price.FastMultiplier : 1;
        if (b.Domestic) multiplier *= List.UsOnlyMultiplier;
        double inRate = multiplier, outRate = multiplier;
        if (!b.IsAggregate && price.LongContext is { } lc && t.Prompt > lc.Above)
        {
            inRate *= lc.Input;
            outRate *= lc.Output;
        }
        var cost = (t.Input * price.Input * inRate
                    + (t.CacheWrite - @long) * price.CacheWrite * inRate
                    + @long * price.CacheWriteLong * inRate
                    + t.CacheRead * price.CacheRead * inRate
                    + t.Output * price.Output * outRate) / 1_000_000
                   + Math.Max(0, b.WebSearches) * List.WebSearch;
        var savings = t.CacheRead * Math.Max(0, price.Input - price.CacheRead) * inRate / 1_000_000;
        return (cost, savings);
    }

    /// <summary>"claude-opus-5-5" reads "Opus 5.5", "gpt-6-astra" reads "GPT-6 Astra"; built from the name so unknown models still read well.</summary>
    public static string DisplayName(string model)
    {
        var id = Normalize(model);
        if (id.Length == 0) return "";
        if (id.StartsWith("claude-", StringComparison.Ordinal))
        {
            // A router's tag after a colon, like ":thinking", is a mode of the same model.
            var family = id.Split(':', 2)[0];
            var parts = family[7..].Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
            // Snapshot dates and version suffixes add nothing to a name.
            parts.RemoveAll(p => p.Length >= 6 && p.All(char.IsDigit));
            parts.RemoveAll(p => p.Length > 1 && p[0] == 'v' && char.IsDigit(p[1]));
            parts.RemoveAll(p => p == "latest");
            static bool IsVersion(string p) => p.All(c => char.IsDigit(c) || c == '.');
            var words = parts.Where(p => !IsVersion(p)).ToList();
            var version = string.Join('.', parts.Where(IsVersion));
            var name = new List<string> { words.Count > 0 ? Cap(words[0]) : "", version };
            name.AddRange(words.Skip(1).Select(Cap));
            return string.Join(' ', name.Where(s => s.Length > 0));
        }
        if (!id.StartsWith("gpt-", StringComparison.Ordinal)) return id;
        var g = id[4..].Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        g.RemoveAll(p => p == "latest");
        g.RemoveAll(p => p.Length >= 6 && p.All(char.IsDigit));
        // A dated snapshot, "-2026-10-01", is the same model.
        if (g.Count >= 4 && int.TryParse(g[^3], out var year) && year is >= 2000 and <= 2100
            && g.TakeLast(2).All(p => p.Length == 2 && p.All(char.IsDigit)))
            g.RemoveRange(g.Count - 3, 3);
        if (g.Count == 0) return "GPT";
        var head = char.IsDigit(g[0][0]) ? "GPT-" + g[0] : "GPT " + Cap(g[0]);
        return string.Join(' ', new[] { head }.Concat(g.Skip(1).Select(Cap)));
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.InvariantCulture) + s[1..];
}

/// <summary>Subscriptions, recognised from what the agents keep on disk.</summary>
internal static class AgentPlans
{
    /// <summary>Matches rateLimitTier/subscriptionType ("max", "max_20x", "pro"…) against the list; a plan newer than the list still shows its own name.</summary>
    public static AgentPlan? Claude(string? subscriptionType, string? rateLimitTier, PriceList list)
    {
        var type = (subscriptionType ?? "").ToLowerInvariant();
        var recorded = (rateLimitTier ?? "").ToLowerInvariant() + " " + type;
        if (list.ClaudePlans.FirstOrDefault(p => recorded.Contains(p.Match)) is { } plan) return new AgentPlan(plan.Name, plan.Monthly);
        var name = type.Replace("claude_", "").Replace('_', ' ').Trim();
        return name.Length == 0 ? null : new AgentPlan(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name), null);
    }

    public static AgentPlan? Codex(string? planType, PriceList list)
    {
        var raw = planType?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(raw)) return null;
        if (list.CodexPlans.FirstOrDefault(p => p.Match == raw) is { } plan) return new AgentPlan(plan.Name, plan.Monthly);
        return new AgentPlan(char.ToUpperInvariant(raw[0]) + raw[1..], null);
    }
}
