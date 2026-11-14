// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Text.Json;
using Lintel.Agents.Logs;
using Lintel.Agents.Pricing;
using Lintel.Core.Agents;

namespace Lintel.Agents.Limits;

/// <summary>Shared helpers of the two server-payload parsers.</summary>
internal static class LimitJson
{
    public static JsonDocument ParseObject(string json)
    {
        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc;
        doc.Dispose();
        throw new InvalidDataException();
    }

    /// <summary>A number, or a numeric string.</summary>
    public static double? Number(JsonElement? v)
    {
        if (v is not { } e) return null;
        return e.ValueKind switch
        {
            JsonValueKind.Number when e.TryGetDouble(out var d) && double.IsFinite(d) => d,
            JsonValueKind.String when double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) => d,
            _ => null,
        };
    }

    /// <summary>ISO-8601 text, or Unix seconds (milliseconds when that large).</summary>
    public static DateTimeOffset? Reset(JsonElement? v)
    {
        if (v is not { } e) return null;
        if (e.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(e.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return date;
        return LogJson.Seconds(Number(e));
    }

    public static AgentLimitWindow? Window(string id, string? scope, string label, JsonElement? data, string percentKey, string resetKey, int? minutes)
    {
        var used = Number(data.Prop(percentKey));
        if (used is not { } u || u < 0 || u > 10000) return null;
        var m = minutes is > 0 ? minutes : null;
        return new AgentLimitWindow(id, LogJson.Kind(m), m, scope, label, Math.Min(100, u), Reset(data.Prop(resetKey)));
    }
}

/// <summary>Claude's OAuth usage payload: five_hour, seven_day, seven_day_sonnet, seven_day_opus; fallback limits[]; credits line.</summary>
internal static class ClaudeUsageParser
{
    public const string AccountId = "claude:default";

    /// <summary>Throws <see cref="InvalidDataException"/> / <see cref="JsonException"/> when no window can be read.</summary>
    public static AgentLimits Parse(string json, AgentPlan? plan, DateTimeOffset now)
    {
        using var doc = LimitJson.ParseObject(json);
        JsonElement? root = doc.RootElement;
        var windows = new List<AgentLimitWindow>();
        void Add(AgentLimitWindow? w) { if (w is not null) windows.Add(w); }
        Add(LimitJson.Window("claude.five_hour", null, "5-hour", root.Prop("five_hour"), "utilization", "resets_at", 300));
        Add(LimitJson.Window("claude.seven_day", null, "Weekly", root.Prop("seven_day"), "utilization", "resets_at", 10080));
        Add(LimitJson.Window("claude.seven_day_sonnet", "Sonnet", "Weekly · Sonnet", root.Prop("seven_day_sonnet"), "utilization", "resets_at", 10080));
        Add(LimitJson.Window("claude.seven_day_opus", "Opus", "Weekly · Opus", root.Prop("seven_day_opus"), "utilization", "resets_at", 10080));
        if (windows.Count == 0 && root.Prop("limits") is { ValueKind: JsonValueKind.Array } limits)
        {
            var n = 0;
            foreach (var item in limits.EnumerateArray())
            {
                JsonElement? limit = item;
                var kind = limit.Str("kind");
                var label = kind == "session" ? "5-hour" : kind == "weekly_all" ? "Weekly" : kind ?? "Quota";
                int? minutes = kind == "session" ? 300 : kind == "weekly_all" ? 10080 : null;
                Add(LimitJson.Window($"claude.{kind ?? "limit" + n}", null, label, limit, "percent", "resets_at", minutes));
                n++;
            }
        }
        if (windows.Count == 0) throw new InvalidDataException();

        var spend = root.Prop("spend");
        var extra = root.Prop("extra_usage");
        var balance = Money(spend.Prop("balance"));
        var reason = spend.Str("disabled_reason") ?? extra.Str("disabled_reason");
        var enabled = spend.IsTrue("enabled") || extra.IsTrue("is_enabled");
        // Claude's cap is not a wallet balance: show cash only when the service supplies a balance.
        var credits = balance is not null ? "Credits · " + balance
            : reason == "out_of_credits" ? "Extra usage · out of credits"
            : enabled ? "Extra usage on · balance not reported" : "Extra usage off";
        return new AgentLimits(AccountId, AgentProvider.Claude, windows, now, LimitSource.Account, plan, credits,
            windows.Any(w => w.UsedPercent >= 100), false, null, null);
    }

    private static string? Money(JsonElement? money)
    {
        var currency = money.Str("currency");
        if (money is null || string.IsNullOrWhiteSpace(currency)) return null;
        var exponent = LimitJson.Number(money.Prop("exponent"));
        if (exponent is not { } ex || ex < 0 || ex > 6 || ex % 1 != 0) return null;
        if (!decimal.TryParse(Text(money.Prop("amount_minor")), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount < 0) return null;
        for (var i = 0; i < ex; i++) amount /= 10;
        return (currency == "USD" ? "$" : currency + " ") + amount.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static string? Text(JsonElement? v) => v is { } e ? e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString() : null;
}

/// <summary>Codex <c>account/rateLimits/read</c> result: rateLimits and/or rateLimitsByLimitId (primary/secondary windows, credits, blocked flags).</summary>
internal static class CodexUsageParser
{
    public static AgentLimits Parse(string json, string accountId, PriceList prices, DateTimeOffset now)
    {
        using var doc = LimitJson.ParseObject(json);
        JsonElement? root = doc.RootElement;
        var blocked = root.Prop("ordinaryUsageAllowed") is { ValueKind: JsonValueKind.False };
        var windows = new List<AgentLimitWindow>();
        string? plan = null, credits = null;

        var buckets = new List<(string Key, JsonElement? Value)>();
        if (root.Prop("rateLimitsByLimitId") is { ValueKind: JsonValueKind.Object } byId)
            buckets.AddRange(byId.EnumerateObject().Select(p => (p.Name, (JsonElement?)p.Value)));
        if (buckets.Count == 0) buckets.Add(("codex", root.Prop("rateLimits")));

        foreach (var (key, value) in buckets.OrderBy(b => b.Key == "codex" ? "" : b.Key, StringComparer.Ordinal))
        {
            if (value is not { ValueKind: JsonValueKind.Object }) continue;
            plan ??= value.Str("planType");
            var title = value.Str("limitName");
            if (string.IsNullOrWhiteSpace(title) && key != "codex") title = key;
            foreach (var slot in new[] { "primary", "secondary" })
            {
                var window = value.Prop(slot);
                int? minutes = LimitJson.Number(window.Prop("windowDurationMins")) is { } m ? (int)m : null;
                var label = WindowLabel(minutes);
                if (!string.IsNullOrWhiteSpace(title)) label = title + " · " + label;
                var id = key == "codex" ? $"codex.{minutes?.ToString() ?? slot}" : $"codex.{key}.{minutes?.ToString() ?? slot}";
                if (LimitJson.Window(id, string.IsNullOrWhiteSpace(title) ? null : title, label, window, "usedPercent", "resetsAt", minutes) is { } w)
                    windows.Add(w);
            }
            if (value.Prop("credits") is { ValueKind: JsonValueKind.Object } cr && (key == "codex" || credits is null))
            {
                JsonElement? c = cr;
                credits = c.IsTrue("unlimited") ? "Credits · unlimited"
                    : c.Prop("balance") is { } b ? "Credits · " + (b.ValueKind == JsonValueKind.String ? b.GetString() : b.ToString())
                    : c.Prop("hasCredits") is { ValueKind: JsonValueKind.False } ? "No extra credits" : "Credit balance not reported";
            }
            blocked |= value.Prop("rateLimitReachedType") is not null || value.IsTrue("spendControlReached");
        }
        if (windows.Count == 0) throw new InvalidDataException();
        return new AgentLimits(accountId, AgentProvider.Codex, windows, now, LimitSource.Account, AgentPlans.Codex(plan, prices),
            credits ?? "Credit balance not reported", blocked, false, null, null);
    }

    /// <summary>"Weekly", "Daily", "3 days", "1 hour", "5-hour", "90 minutes", "Quota".</summary>
    public static string WindowLabel(int? minutes) => minutes switch
    {
        null or <= 0 => "Quota",
        10080 => "Weekly",
        1440 => "Daily",
        var m when m % 1440 == 0 => $"{m / 1440}-day",
        var m when m % 60 == 0 => $"{m / 60}-hour",
        var m => $"{m}-minute",
    };
}

/// <summary>
/// Claude desktop's <c>plan-usage-history.json</c> (version 1 or 2): samples[] {t (ms), org, u:{fh,sd,so,sn}}. Percentages only, no reset times
/// ("reset time not reported"). A session (fh) sample older than 5 h is dropped; whether the newest sample is stale (30 min) is the caller's call.
/// </summary>
internal static class ClaudeAppHistory
{
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SessionLength = TimeSpan.FromHours(5);
    private const int MaximumSize = 4 << 20;

    private static readonly (string Key, string Id, string Label, string? Scope, int Minutes)[] Windows =
    [
        ("fh", "claude.five_hour", "5-hour", null, 300),
        ("sd", "claude.seven_day", "Weekly", null, 10080),
        ("sn", "claude.seven_day_sonnet", "Weekly · Sonnet", "Sonnet", 10080),
        ("so", "claude.seven_day_opus", "Weekly · Opus", "Opus", 10080),
    ];

    /// <summary>Null for a file this reader does not know (other version, too big, unreadable) or with nothing usable.</summary>
    public static AgentLimits? Parse(ReadOnlySpan<byte> json, DateTimeOffset now)
    {
        if (json.Length > MaximumSize) return null;
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            JsonElement? root = doc.RootElement;
            var version = root.Dbl("version");
            if (version is not (1 or 2) || root.Prop("samples") is not { ValueKind: JsonValueKind.Array } samples) return null;
            // Newest reading per window: the file is oldest-first, but never trust that.
            var latest = new Dictionary<string, (DateTimeOffset At, double Used)>();
            DateTimeOffset newest = DateTimeOffset.MinValue;
            foreach (var s in samples.EnumerateArray())
            {
                JsonElement? sample = s;
                if (sample.Dbl("t") is not { } ms || ms <= 0) continue;
                var at = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Min(ms, 253402300799000));
                if (at > now.AddMinutes(5)) continue;
                // The first version kept its two windows beside the time.
                var values = version == 1 ? sample : sample.Prop("u");
                foreach (var w in Windows)
                    if (LimitJson.Number(values.Prop(w.Key)) is { } used && (!latest.TryGetValue(w.Key, out var prev) || at >= prev.At))
                        latest[w.Key] = (at, Math.Clamp(used, 0, 100));
                if (at > newest) newest = at;
            }
            var result = new List<AgentLimitWindow>();
            foreach (var w in Windows)
            {
                if (!latest.TryGetValue(w.Key, out var l)) continue;
                if (w.Key == "fh" && now - l.At > SessionLength) continue;
                result.Add(new AgentLimitWindow(w.Id, LogJson.Kind(w.Minutes), w.Minutes, w.Scope, w.Label, l.Used, null));
            }
            if (result.Count == 0) return null;
            var observed = latest.Values.Max(v => v.At);
            return new AgentLimits(ClaudeUsageParser.AccountId, AgentProvider.Claude, result, observed, LimitSource.ClaudeApp, null, null, false, false, null, null);
        }
        catch (JsonException) { return null; }
    }

    public static string HistoryPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "plan-usage-history.json");

    /// <summary>Reads the file read-only with full sharing; nothing is ever written or sent.</summary>
    public static AgentLimits? Read(string path, DateTimeOffset now)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaximumSize) return null;
            var buffer = new byte[stream.Length];
            stream.ReadExactly(buffer);
            return Parse(buffer, now);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
