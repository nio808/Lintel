// SPDX-License-Identifier: GPL-3.0-or-later
// Portions adapted from vorssaint-utils, Copyright (C) 2026 Vorssaint, GPL-3.0-or-later.
using System.Globalization;
using System.Text.Json;

namespace Lintel.Agents.Pricing;

/// <summary>Premium on the whole request once the prompt passes <paramref name="Above"/> tokens.</summary>
internal sealed record LongContext(int Above, double Input, double Output);

/// <summary>US dollars per million tokens from each provider's published API list.</summary>
internal sealed record ModelPrice(string Id, double Input, double Output, double CacheRead, double CacheWrite, double CacheWriteLong,
                                  double FastMultiplier = 1, LongContext? LongContext = null);

internal sealed record PlanRule(string Match, string Name, double? Monthly);

/// <summary>
/// Every price the app knows, kept out of the code: a copy ships embedded, a newer one published with the project replaces it.
/// A list that breaks any rule is refused as a whole and the last good one stays.
/// </summary>
internal sealed record PriceList(DateOnly Updated, IReadOnlyList<ModelPrice> Claude, IReadOnlyList<ModelPrice> Codex,
                                 IReadOnlyList<PlanRule> ClaudePlans, IReadOnlyList<PlanRule> CodexPlans,
                                 double WebSearch, double UsOnlyMultiplier)
{
    public const int Schema = 1;
    public const int MaximumSize = 256 << 10;

    public static PriceList Empty { get; } = new(DateOnly.MinValue, [], [], [], [], 0, 1);

    /// <summary>Null when the data is too big, not JSON, another schema, or violates any bound (duplicate ids included).</summary>
    public static PriceList? Decode(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaximumSize) return null;
        try
        {
            using var doc = JsonDocument.Parse(utf8.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Number(Get(root, "schema"), 1, 1000) != Schema) return null;
            if (Get(root, "updated") is not { ValueKind: JsonValueKind.String } u || !Day(u.GetString(), out var updated)) return null;
            if (Get(root, "claude") is not { ValueKind: JsonValueKind.Object } claude
                || Get(root, "codex") is not { ValueKind: JsonValueKind.Object } codex) return null;
            if (Models(Get(claude, "models"), "claude-") is not { } claudeModels || Models(Get(codex, "models"), null) is not { } codexModels) return null;
            if (Plans(Get(claude, "plans")) is not { } claudePlans || Plans(Get(codex, "plans")) is not { } codexPlans) return null;
            if (Number(Get(claude, "webSearch"), 0, 1) is not { } web || Number(Get(claude, "usOnlyMultiplier"), 1, 3) is not { } us) return null;
            return new PriceList(updated, claudeModels, codexModels, claudePlans, codexPlans, web, us);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The newer of two lists; on the same day the downloaded one wins (it may carry a correction).</summary>
    public static PriceList? Newer(PriceList? shipped, PriceList? downloaded) =>
        downloaded is null ? shipped : shipped is null ? downloaded : downloaded.Updated >= shipped.Updated ? downloaded : shipped;

    private static JsonElement? Get(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v : null;

    private static double? Number(JsonElement? v, double min, double max) =>
        v is { ValueKind: JsonValueKind.Number } n && n.TryGetDouble(out var d) && double.IsFinite(d) && d >= min && d <= max ? d : null;

    private static bool Day(string? text, out DateOnly day) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day) && day.Year is >= 2024 and <= 2100;

    /// <summary>Lowercase letters, digits, dots, dashes, underscores: the shape of every model and plan id.</summary>
    private static bool Identifier(string? s, int length) =>
        s is { Length: > 0 } && s.Length <= length && s.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' or '_');

    private static List<ModelPrice>? Models(JsonElement? value, string? prefix)
    {
        if (value is not { ValueKind: JsonValueKind.Array } array || array.GetArrayLength() is < 1 or > 500) return null;
        var result = new List<ModelPrice>();
        var seen = new HashSet<string>();
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || Get(entry, "id") is not { ValueKind: JsonValueKind.String } idEl) return null;
            var id = idEl.GetString();
            if (!Identifier(id, 64) || !seen.Add(id!)) return null;
            if (prefix is not null ? !id!.StartsWith(prefix, StringComparison.Ordinal) : id!.StartsWith("claude-", StringComparison.Ordinal)) return null;
            if (Number(Get(entry, "input"), 0, 1000) is not { } input || Number(Get(entry, "output"), 0, 1000) is not { } output
                || Number(Get(entry, "cacheRead"), 0, 1000) is not { } cacheRead) return null;
            // Fields left out fall back to plain input pricing, as providers without separate cache writes bill them.
            var cacheWrite = Get(entry, "cacheWrite") is null ? input : Number(Get(entry, "cacheWrite"), 0, 1000);
            var cacheWriteLong = Get(entry, "cacheWriteLong") is null ? cacheWrite : Number(Get(entry, "cacheWriteLong"), 0, 1000);
            var fast = Get(entry, "fastMultiplier") is null ? 1 : Number(Get(entry, "fastMultiplier"), 1, 10);
            if (cacheWrite is null || cacheWriteLong is null || fast is null) return null;
            LongContext? lc = null;
            if (Get(entry, "longContext") is { } l)
            {
                if (l.ValueKind != JsonValueKind.Object || Number(Get(l, "above"), 1000, 10_000_000) is not { } above
                    || Number(Get(l, "inputMultiplier"), 1, 10) is not { } li || Number(Get(l, "outputMultiplier"), 1, 10) is not { } lo) return null;
                lc = new LongContext((int)above, li, lo);
            }
            result.Add(new ModelPrice(id!, input, output, cacheRead, cacheWrite.Value, cacheWriteLong.Value, fast.Value, lc));
        }
        return result;
    }

    private static List<PlanRule>? Plans(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Array } array || array.GetArrayLength() > 50) return null;
        var result = new List<PlanRule>();
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) return null;
            var match = Get(entry, "match") is { ValueKind: JsonValueKind.String } m ? m.GetString() : null;
            var name = (Get(entry, "name") is { ValueKind: JsonValueKind.String } n ? n.GetString() : null)?.Trim();
            if (!Identifier(match, 40) || name is not { Length: >= 1 and <= 24 }) return null;
            double? monthly = null;
            if (Get(entry, "monthly") is not null && (monthly = Number(Get(entry, "monthly"), 0, 100_000)) is null) return null;
            result.Add(new PlanRule(match!, name, monthly));
        }
        return result;
    }
}
