// SPDX-License-Identifier: GPL-3.0-or-later
// Portions adapted from vorssaint-utils, Copyright (C) 2026 Vorssaint, GPL-3.0-or-later.
using Lintel.Agents.Logs;
using Lintel.Agents.Pricing;
using Lintel.Core.Agents;

namespace Lintel.Agents.Usage;

internal readonly record struct BucketKey(long Hour, AgentProvider Provider, string Model, string Project);

internal sealed class Bucket
{
    public AgentTokens Tokens;
    public double Cost;
    public double Savings;
    public int Requests;
    public int Unpriced;

    public AgentTotals ToTotals() => new(Tokens, Cost, Savings, Requests, Unpriced);
}

/// <summary>One remembered response as saved in the cache (hash of its key, where it was counted, what was counted).</summary>
internal readonly record struct SeenEntry(ulong Hash, BucketKey Key, Billable Billable, string Model, double? ReportedCost);

/// <summary>What adding a usage entry changed, for the live turn it counts toward.</summary>
internal readonly record struct UsageDelta(bool Changed, AgentTokens Tokens, double Cost, int Unpriced);

/// <summary>
/// Hourly aggregates keyed (hour, provider, model, project) with the archive's 30-day retention. A response repeated
/// across content blocks or files (key) is counted once, keeping the per-field maximum of its token counts.
/// </summary>
internal sealed class UsageStore(AgentPricing pricing)
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private const int MaxRemembered = 100_000;

    // remembered keys are a global FIFO of 100k responses (not per-file LRU) so a resumed session's copied
    // history in a new file is also caught. The newest PersistedSeen are saved in the cache so a log rewritten from
    // byte 0 after a restart is still recognised; older responses than that can double count on such a rewrite.
    public const int PersistedSeen = 30_000;
    private readonly Dictionary<ulong, Seen> seen = [];
    private readonly Queue<ulong> order = new();

    private sealed record Seen(BucketKey Key, Billable Billable, string Model, double? ReportedCost);

    /// <summary>Newest remembered responses, oldest first, for the cache.</summary>
    public List<SeenEntry> ExportSeen() =>
        order.Skip(Math.Max(0, order.Count - PersistedSeen)).Where(seen.ContainsKey)
             .Select(h => { var s = seen[h]; return new SeenEntry(h, s.Key, s.Billable, s.Model, s.ReportedCost); }).ToList();

    /// <summary>Restores remembered responses whose aggregates came from the same cache.</summary>
    public void LoadSeen(IEnumerable<SeenEntry> entries)
    {
        foreach (var e in entries)
        {
            if (seen.ContainsKey(e.Hash)) continue;
            seen[e.Hash] = new Seen(e.Key, e.Billable, e.Model, e.ReportedCost);
            order.Enqueue(e.Hash);
        }
    }

    public AgentPricing Pricing { get; set; } = pricing;
    public Dictionary<BucketKey, Bucket> Buckets { get; } = [];

    public static long HourOf(DateTimeOffset time) => (long)Math.Floor(time.ToUnixTimeSeconds() / 3600.0);

    public UsageDelta Add(LogEntry.Usage u, DateTimeOffset now)
    {
        if (u.Date < now - Retention || u.Date > now.AddDays(1)) return default;
        var key = new BucketKey(HourOf(u.Date), u.Provider, u.Model, u.Project);
        var hash = Hash(u.Key);
        if (seen.TryGetValue(hash, out var old))
        {
            // A streamed response repeats per content block: token counts only grow, and later chunks may add
            // the 1-hour cache write split or web searches an earlier chunk did not carry yet.
            var o = old.Billable;
            var merged = o.Tokens.Max(u.Billable.Tokens);
            var updated = new Billable(merged, Math.Max(o.LongCacheWrite, u.Billable.LongCacheWrite), o.Fast | u.Billable.Fast,
                o.Domestic | u.Billable.Domestic, Math.Max(o.WebSearches, u.Billable.WebSearches), o.IsAggregate);
            if (updated == o) return default;
            var (oc, os) = CostOf(old.Billable, old.Model, old.ReportedCost);
            var (nc, ns) = CostOf(updated, old.Model, u.ReportedCost ?? old.ReportedCost);
            var bucket = Buckets[old.Key];
            var dTokens = merged - o.Tokens;
            bucket.Tokens += dTokens;
            bucket.Cost += (nc ?? 0) - (oc ?? 0);
            bucket.Savings += ns - os;
            seen[hash] = old with { Billable = updated, ReportedCost = u.ReportedCost ?? old.ReportedCost };
            return new(true, dTokens, (nc ?? 0) - (oc ?? 0), 0);
        }

        var (cost, savings) = CostOf(u.Billable, u.Model, u.ReportedCost);
        if (!Buckets.TryGetValue(key, out var b)) Buckets[key] = b = new Bucket();
        b.Tokens += u.Billable.Tokens;
        b.Cost += cost ?? 0;
        b.Savings += savings;
        b.Requests += u.Requests;
        var unpriced = cost is null ? 1 : 0;
        b.Unpriced += unpriced;
        seen[hash] = new Seen(key, u.Billable, u.Model, u.ReportedCost);
        order.Enqueue(hash);
        if (order.Count > MaxRemembered) seen.Remove(order.Dequeue());
        return new(true, u.Billable.Tokens, cost ?? 0, unpriced);
    }

    /// <summary>List price when the model is known; otherwise what the provider itself recorded (OpenCode), else unpriced.</summary>
    private (double? Cost, double Savings) CostOf(Billable b, string model, double? reported)
    {
        var (cost, savings) = Pricing.Cost(b, model);
        return (cost ?? reported, savings);
    }

    /// <summary>Drops buckets older than the retention; true if any went.</summary>
    public bool Prune(DateTimeOffset now)
    {
        var floor = HourOf(now - Retention);
        var stale = Buckets.Keys.Where(k => k.Hour < floor).ToList();
        foreach (var k in stale) Buckets.Remove(k);
        return stale.Count > 0;
    }

    /// <summary>Loads persisted aggregates (cache).</summary>
    public void Load(BucketKey key, Bucket bucket)
    {
        if (Buckets.TryGetValue(key, out var existing))
        {
            existing.Tokens += bucket.Tokens;
            existing.Cost += bucket.Cost;
            existing.Savings += bucket.Savings;
            existing.Requests += bucket.Requests;
            existing.Unpriced += bucket.Unpriced;
        }
        else Buckets[key] = bucket;
    }

    private static ulong Hash(string text)
    {
        var h = 14695981039346656037UL;
        foreach (var c in text) { h ^= c; h *= 1099511628211UL; }
        return h;
    }
}
