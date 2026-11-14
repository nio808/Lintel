// SPDX-License-Identifier: GPL-3.0-or-later
// Portions adapted from vorssaint-utils, Copyright (C) 2026 Vorssaint, GPL-3.0-or-later.
using System.Collections.ObjectModel;
using Lintel.Agents.Pricing;
using Lintel.Core.Agents;

namespace Lintel.Agents.Usage;

internal sealed record UsageView(
    IReadOnlyDictionary<AgentPeriod, AgentPeriodUsage> Periods, IReadOnlyList<AgentBucket> Days,
    IReadOnlyList<AgentBucket> Hours, IReadOnlyList<AgentProvider> Seen);

/// <summary>Pure: buckets → periods, 30 local days, last 24 hours, and shares (top by API value).</summary>
internal static class UsageSummary
{
    public const int TopShares = 20;
    private static readonly AgentProvider[] Providers = Enum.GetValues<AgentProvider>();

    /// <summary>
    /// Today = since local midnight; Week = the last 7 local days including today; Month = the last 30.
    /// Days/Hours carry an entry for every provider (zero when idle); a period's ByProvider only lists providers with usage.
    /// </summary>
    public static UsageView Build(IEnumerable<KeyValuePair<BucketKey, Bucket>> buckets, DateTimeOffset now, TimeZoneInfo zone,
                                  Func<AgentProvider, bool>? include = null)
    {
        include ??= _ => true;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(local.DateTime);
        var days = new Dictionary<DateOnly, Dictionary<AgentProvider, AgentTotals>>();
        var hours = new Dictionary<long, Dictionary<AgentProvider, AgentTotals>>();
        var nowHour = UsageStore.HourOf(now);
        var seen = new HashSet<AgentProvider>();
        var period = new Dictionary<AgentPeriod, Acc>
        {
            [AgentPeriod.Today] = new(), [AgentPeriod.Week] = new(), [AgentPeriod.Month] = new(),
        };

        foreach (var (key, bucket) in buckets)
        {
            if (!include(key.Provider)) continue;
            var totals = bucket.ToTotals();
            if (totals.Tokens.Total > 0 || totals.Requests > 0 || totals.Cost > 0) seen.Add(key.Provider);
            var start = DateTimeOffset.FromUnixTimeSeconds(key.Hour * 3600);
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime);
            var age = today.DayNumber - date.DayNumber;
            if (age < 0 || age >= 30) continue;

            Add(days, date, key.Provider, totals);
            if (key.Hour > nowHour - 24 && key.Hour <= nowHour) Add(hours, key.Hour, key.Provider, totals);
            if (age == 0) period[AgentPeriod.Today].Add(key, totals);
            if (age < 7) period[AgentPeriod.Week].Add(key, totals);
            period[AgentPeriod.Month].Add(key, totals);
        }

        var dayList = Enumerable.Range(0, 30).Select(i => today.AddDays(i - 29)).Select(d =>
            new AgentBucket(Midnight(d, zone), Full(days.GetValueOrDefault(d)))).ToList();
        var hourList = Enumerable.Range(0, 24).Select(i => nowHour - 23 + i).Select(h =>
            new AgentBucket(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(h * 3600), zone), Full(hours.GetValueOrDefault(h)))).ToList();
        var periods = new ReadOnlyDictionary<AgentPeriod, AgentPeriodUsage>(period.ToDictionary(p => p.Key, p => p.Value.Finish()));
        return new UsageView(periods, dayList, hourList, Providers.Where(seen.Contains).ToList());
    }

    private static DateTimeOffset Midnight(DateOnly date, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        // A DST gap at midnight (some zones) has no such local time: move to the first valid instant.
        if (zone.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }

    private static void Add<TKey>(Dictionary<TKey, Dictionary<AgentProvider, AgentTotals>> map, TKey key, AgentProvider provider, AgentTotals totals)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var byProvider)) map[key] = byProvider = [];
        byProvider[provider] = byProvider.TryGetValue(provider, out var t) ? t + totals : totals;
    }

    private static ReadOnlyDictionary<AgentProvider, AgentTotals> Full(Dictionary<AgentProvider, AgentTotals>? map) =>
        new(Providers.ToDictionary(p => p, p => map is not null && map.TryGetValue(p, out var t) ? t : AgentTotals.Zero));

    private sealed class Acc
    {
        private AgentTotals total = AgentTotals.Zero;
        private readonly Dictionary<AgentProvider, AgentTotals> byProvider = [];
        private readonly Dictionary<(AgentProvider, string), AgentTotals> models = [];
        private readonly Dictionary<string, AgentTotals> projects = [];

        public void Add(BucketKey key, AgentTotals t)
        {
            total += t;
            byProvider[key.Provider] = byProvider.TryGetValue(key.Provider, out var p) ? p + t : t;
            var model = key.Model.Length == 0 ? "Unknown" : AgentPricing.DisplayName(key.Model) is { Length: > 0 } n ? n : key.Model;
            models[(key.Provider, model)] = models.TryGetValue((key.Provider, model), out var m) ? m + t : t;
            var project = key.Project.Length == 0 ? "Unknown" : key.Project;
            projects[project] = projects.TryGetValue(project, out var j) ? j + t : t;
        }

        public AgentPeriodUsage Finish() => new(total, new ReadOnlyDictionary<AgentProvider, AgentTotals>(byProvider),
            Sort(models.Select(p => new AgentShare(p.Key.Item2, p.Key.Item1, p.Value))),
            Sort(projects.Select(p => new AgentShare(p.Key, null, p.Value))));

        private static IReadOnlyList<AgentShare> Sort(IEnumerable<AgentShare> shares) =>
            shares.OrderByDescending(s => s.Totals.Cost).ThenByDescending(s => s.Totals.Tokens.Total)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Take(TopShares).ToList();
    }
}
