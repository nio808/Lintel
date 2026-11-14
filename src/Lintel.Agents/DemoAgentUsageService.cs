// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Windows.Threading;
using Lintel.Agents.Pricing;
using Lintel.Agents.Usage;
using Lintel.Core.Agents;
using Lintel.Core.Modules;

namespace Lintel.Agents;

/// <summary>
/// --demo: canned data, touches no accounts, no files, no network. Two accounts, 30 days of synthetic usage, and one live
/// session that finishes every 90 s (raising a Finished event) and is replaced by the next one.
/// </summary>
internal sealed class DemoAgentUsageService : IAgentUsageService
{
    private static readonly TimeSpan CycleLength = TimeSpan.FromSeconds(90);

    private readonly IModuleContext ctx;
    private readonly UsageStore store;
    private readonly AgentAccount[] accounts =
    [
        new("claude:default", AgentProvider.Claude, "Claude", null, true),
        new("codex:default", AgentProvider.Codex, "Codex", null, true),
    ];
    private DispatcherTimer? timer;
    private DateTimeOffset cycleStart;
    private int cycle;
    private bool started;
    private AgentUsageSnapshot current = AgentUsageSnapshot.Empty;

    public DemoAgentUsageService(IModuleContext context)
    {
        ctx = context;
        store = new UsageStore(new AgentPricing(PriceSource.Embedded()));
        Seed(context.Time.GetUtcNow());
        current = Build(context.Time.GetUtcNow(), loaded: false);
    }

    public AgentUsageSnapshot Current => current;

    public event EventHandler<AgentUsageSnapshot>? SnapshotChanged;
    public event EventHandler<AgentUsageEvent>? EventRaised;

    public void Start()
    {
        if (started) return;
        started = true;
        cycleStart = ctx.Time.GetUtcNow() - TimeSpan.FromSeconds(20);
        Publish();
        timer = new DispatcherTimer(DispatcherPriority.Background, ctx.Ui) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    public void Stop()
    {
        timer?.Stop();
        timer = null;
        started = false;
    }

    public void SetSectionVisible(bool visible) { }

    public Task RefreshLimitsAsync(string? accountId = null) => Task.CompletedTask;

    public AgentAccount AddCodexHome(string folder, string displayName) =>
        new("codex:" + displayName.ToLowerInvariant().Replace(' ', '-'), AgentProvider.Codex, displayName, folder, false);

    public void RemoveAccount(string accountId) { }

    public void Dispose() => Stop();

    private void Tick()
    {
        var now = ctx.Time.GetUtcNow();
        if (now - cycleStart >= CycleLength)
        {
            var project = Projects[cycle % Projects.Length];
            EventRaised?.Invoke(this, new AgentUsageEvent.Finished(cycle % 2 == 0 ? AgentProvider.Claude : AgentProvider.Codex,
                CycleLength, 0.84 + (cycle % 3) * 0.37, 91_000 + cycle * 4_000, project));
            cycle++;
            cycleStart = now;
        }
        Publish();
    }

    private void Publish()
    {
        current = Build(ctx.Time.GetUtcNow(), loaded: true);
        SnapshotChanged?.Invoke(this, current);
    }

    private static readonly string[] Projects = ["lintel", "website", "api-server", "dotfiles"];

    private static readonly string[] ClaudeModels = ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"];
    private static readonly string[] CodexModels = ["gpt-6.1-sol", "gpt-6.1-terra"];

    /// <summary>Deterministic synthetic usage for the last 30 days.</summary>
    private void Seed(DateTimeOffset now)
    {
        var rng = new Random(1234);
        for (var back = 0; back < 30 * 24; back++)
        {
            var hour = now.AddHours(-back);
            var local = TimeZoneInfo.ConvertTime(hour, TimeZoneInfo.Local);
            var recent = back < 3;   // "Today" is never empty, whatever the time of day
            if (!recent && (local.Hour is < 8 or > 23 || (local.DayOfWeek is DayOfWeek.Sunday && rng.Next(3) > 0))) continue;
            if (!recent && rng.Next(3) == 0) continue;
            foreach (var (provider, models) in new[] { (AgentProvider.Claude, ClaudeModels), (AgentProvider.Codex, CodexModels) })
            {
                if (provider == AgentProvider.Codex && rng.Next(2) == 0) continue;
                var model = models[rng.Next(models.Length)];
                var scale = provider == AgentProvider.Claude ? 1.0 : 0.6;
                var tokens = new AgentTokens((long)(rng.Next(20_000, 90_000) * scale), (long)(rng.Next(10_000, 60_000) * scale),
                    (long)(rng.Next(400_000, 2_400_000) * scale), (long)(rng.Next(4_000, 30_000) * scale), 0);
                var billable = new Logs.Billable(tokens);
                var (cost, savings) = store.Pricing.Cost(billable, model);
                store.Buckets[new BucketKey(UsageStore.HourOf(hour), provider, model, Projects[rng.Next(Projects.Length)])] =
                    new Bucket { Tokens = tokens, Cost = cost ?? 0, Savings = savings, Requests = rng.Next(8, 60) };
            }
        }
    }

    private AgentUsageSnapshot Build(DateTimeOffset now, bool loaded)
    {
        var view = UsageSummary.Build(store.Buckets, now, TimeZoneInfo.Local);
        var elapsed = Math.Max(0, (now - cycleStart).TotalSeconds);
        var provider = cycle % 2 == 0 ? AgentProvider.Claude : AgentProvider.Codex;
        var live = loaded
            ? [new AgentLiveSession("demo:live", provider, cycleStart, now, provider == AgentProvider.Claude ? "claude-opus-5-5" : "gpt-6.1-sol",
                Projects[cycle % Projects.Length], new AgentTokens((long)(elapsed * 310), (long)(elapsed * 120), (long)(elapsed * 4200), (long)(elapsed * 55), 0),
                elapsed * 0.0093)]
            : new List<AgentLiveSession>();
        return new AgentUsageSnapshot
        {
            Loaded = loaded,
            Now = now,
            Accounts = accounts,
            Limits = Limits(now),
            Periods = view.Periods,
            Days = view.Days,
            Hours = view.Hours,
            Live = live,
            Seen = [AgentProvider.Claude, AgentProvider.Codex],
            PricesUpdated = store.Pricing.List.Updated,
        };
    }

    private static IReadOnlyList<AgentLimits> Limits(DateTimeOffset now) =>
    [
        new AgentLimits("claude:default", AgentProvider.Claude,
        [
            new("claude.five_hour", LimitKind.Session, 300, null, "5-hour", 38, now.AddMinutes(134)),
            new("claude.seven_day", LimitKind.Weekly, 10080, null, "Weekly", 61, now.AddDays(2).AddHours(5)),
            new("claude.seven_day_opus", LimitKind.Weekly, 10080, "Opus", "Weekly · Opus", 22, now.AddDays(2).AddHours(5)),
        ], now.AddSeconds(-40), LimitSource.Account, new AgentPlan("Max", 100), "Extra usage off", false, false, null, now.AddMinutes(4)),
        new AgentLimits("codex:default", AgentProvider.Codex,
        [
            new("codex.300", LimitKind.Session, 300, null, "5-hour", 12, now.AddMinutes(201)),
            new("codex.10080", LimitKind.Weekly, 10080, null, "Weekly", 47, now.AddDays(4).AddHours(1)),
        ], now.AddSeconds(-90), LimitSource.Account, new AgentPlan("Plus", 20), "Credit balance not reported", false, false, null, now.AddMinutes(3)),
    ];
}
