// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Windows.Threading;
using Lintel.Core.Agents;

namespace Lintel.Agents.UI;

/// <summary>
/// Hand-written fake data for designing the UI without the data service (Preview: <c>agents --design[=scenario]</c>). Scenarios:
/// full (default: healthy + live work), low, blocked, stale, accounts (4 accounts), empty, loading.
/// Live sessions tick every second; the Codex one "finishes" every 90 s. No accounts, files or network are touched.
/// </summary>
internal sealed class DesignAgentService : IAgentUsageService
{
    private readonly string scenario;
    private readonly TimeProvider time;
    private readonly DispatcherTimer timer;
    private readonly List<AgentAccount> extraAccounts = [];
    private readonly DateTimeOffset started;
    private DateTimeOffset? refreshedAt;
    private int finishes;

    public DesignAgentService(string scenario, TimeProvider time, Dispatcher ui)
    {
        this.scenario = scenario;
        this.time = time;
        started = time.GetLocalNow();
        timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        if (scenario == "accounts")
        {
            extraAccounts.Add(new("codex:work", AgentProvider.Codex, "Work", @"D:\codex-homes\work", false));
            extraAccounts.Add(new("codex:side", AgentProvider.Codex, "Side project", @"D:\codex-homes\side", false));
        }
        Current = Build();
    }

    public AgentUsageSnapshot Current { get; private set; }

    public event EventHandler<AgentUsageSnapshot>? SnapshotChanged;
    public event EventHandler<AgentUsageEvent>? EventRaised;

    public void Start() => timer.Start();

    public void Stop() => timer.Stop();

    public void Dispose() => timer.Stop();

    public void SetSectionVisible(bool visible) { }

    public async Task RefreshLimitsAsync(string? accountId = null)
    {
        await Task.Delay(900);
        refreshedAt = time.GetLocalNow();
        Publish();
    }

    public AgentAccount AddCodexHome(string folder, string displayName)
    {
        var account = new AgentAccount("codex:" + displayName.ToLowerInvariant().Replace(' ', '-'), AgentProvider.Codex, displayName, folder, false);
        extraAccounts.Add(account);
        Publish();
        return account;
    }

    public void RemoveAccount(string accountId)
    {
        extraAccounts.RemoveAll(a => a.Id == accountId);
        Publish();
    }

    private void Tick()
    {
        var elapsed = time.GetLocalNow() - started;
        // A notice right away, then one every 90 s, so the notice mapping is visible while designing.
        if (scenario is "full" or "accounts" && elapsed.TotalSeconds >= 1 + 90 * finishes)
        {
            finishes++;
            EventRaised?.Invoke(this, new AgentUsageEvent.Finished(AgentProvider.Codex, TimeSpan.FromSeconds(48 + 90), 0.31, 412_000, "api-server"));
        }
        if (scenario == "low" && finishes == 0)
        {
            finishes++;
            var claude = Current.Limits[0];
            EventRaised?.Invoke(this, new AgentUsageEvent.LimitWarning(claude, claude.Windows[0], 20));
        }
        Publish();
    }

    private void Publish()
    {
        Current = Build();
        SnapshotChanged?.Invoke(this, Current);
    }

    private AgentUsageSnapshot Build()
    {
        var now = time.GetLocalNow();
        if (scenario == "loading")
            return AgentUsageSnapshot.Empty with { Now = now };
        if (scenario == "empty")
            return AgentUsageSnapshot.Empty with { Now = now, Loaded = true };

        var claude = new AgentAccount("claude:default", AgentProvider.Claude, "Claude", null, true);
        var codex = new AgentAccount("codex:default", AgentProvider.Codex, "Codex", null, true);
        var accounts = new List<AgentAccount> { claude, codex };
        accounts.AddRange(extraAccounts);

        var (hours, days) = Buckets(now);
        var live = scenario is "full" or "accounts" ? Live(now) : [];
        return new AgentUsageSnapshot
        {
            Loaded = true,
            Now = now,
            Accounts = accounts,
            Limits = Limits(now, accounts),
            Periods = new ReadOnlyDictionary<AgentPeriod, AgentPeriodUsage>(new Dictionary<AgentPeriod, AgentPeriodUsage>
            {
                [AgentPeriod.Today] = Usage(days.TakeLast(1)),
                [AgentPeriod.Week] = Usage(days.TakeLast(7)),
                [AgentPeriod.Month] = Usage(days),
            }),
            Days = days,
            Hours = hours,
            Live = live,
            Seen = [AgentProvider.Claude, AgentProvider.Codex, AgentProvider.Copilot, AgentProvider.OpenCode],
            PricesUpdated = DateOnly.FromDateTime(now.Date.AddDays(-3)),
        };
    }

    private IReadOnlyList<AgentLimits> Limits(DateTimeOffset now, List<AgentAccount> accounts)
    {
        var observed = refreshedAt ?? now.AddMinutes(-2);
        var next = now.AddMinutes(3);
        var claudeMax = new AgentPlan("Max", 200);
        var codexPlus = new AgentPlan("Plus", 20);
        AgentLimitWindow Session(string id, double used, TimeSpan resets) => new(id, LimitKind.Session, 300, null, "5-hour", used, now + resets);
        AgentLimitWindow Weekly(string id, double used, TimeSpan resets, string? scope = null) =>
            new(id, LimitKind.Weekly, 10080, scope, scope is null ? "Weekly" : $"Weekly · {scope}", used, now + resets);

        var claudeWindows = scenario switch
        {
            "low" => new[] { Session("five_hour", 92, new(1, 6, 0)), Weekly("seven_day", 83, new(2, 5, 0, 0)), Weekly("seven_day_opus", 61, new(2, 5, 0, 0), "Opus") },
            _ => [Session("five_hour", 41, new(2, 14, 0)), Weekly("seven_day", 28, new(3, 17, 0, 0)), Weekly("seven_day_opus", 12, new(3, 17, 0, 0), "Opus")],
        };
        var claudeLimits = scenario is "stale" or "blocked"
            ? new AgentLimits("claude:default", AgentProvider.Claude, claudeWindows, now.AddMinutes(-14), LimitSource.Account, claudeMax, null, false,
                true, "Couldn't reach Anthropic", now.AddMinutes(4))
            : new AgentLimits("claude:default", AgentProvider.Claude, claudeWindows, observed, LimitSource.Account, claudeMax, null, false, false, null, next);

        var codexLimits = scenario switch
        {
            "blocked" => new AgentLimits("codex:default", AgentProvider.Codex,
                [Session("primary", 100, new(0, 38, 0)), Weekly("secondary", 64, new(2, 6, 0, 0))], observed, LimitSource.Account, codexPlus, "Credits · 0", true, false, null, next),
            "stale" => new AgentLimits("codex:default", AgentProvider.Codex, [], now.AddMinutes(-1), LimitSource.Account, null, null, false, false,
                "Codex CLI not found — install Codex and run `codex login`", now.AddMinutes(5)),
            _ => new AgentLimits("codex:default", AgentProvider.Codex,
                [Session("primary", 14, new(4, 2, 0)), Weekly("secondary", 77, new(2, 6, 0, 0))], observed, LimitSource.Account, codexPlus, "Credits · 0", false, false, null, next),
        };

        var list = new List<AgentLimits> { claudeLimits, codexLimits };
        foreach (var extra in accounts.Where(a => !a.IsDefault))
        {
            list.Add(extra.Id == "codex:side"
                ? new AgentLimits(extra.Id, AgentProvider.Codex, [Session("primary", 35, new(3, 10, 0)), Weekly("secondary", 91, new(1, 2, 0, 0))],
                    observed, LimitSource.SessionLog, codexPlus, null, false, false, null, next)
                : new AgentLimits(extra.Id, AgentProvider.Codex, [Session("primary", 22, new(1, 48, 0)), Weekly("secondary", 40, new(5, 1, 0, 0))],
                    observed, LimitSource.Account, new AgentPlan("Pro", 200), "Credits · 1,250", false, false, null, next));
        }
        return list;
    }

    private IReadOnlyList<AgentLiveSession> Live(DateTimeOffset now)
    {
        var seconds = (now - started).TotalSeconds;
        var codexAge = 48 + seconds % 90;
        return
        [
            new("design:claude", AgentProvider.Claude, started.AddSeconds(-252), now, "claude-opus-4-1-20250805", "lintel",
                Tokens(1_180_000 + (long)(seconds * 21_000), claude: true), 0.84 + seconds * 0.012),
            new("design:codex", AgentProvider.Codex, now.AddSeconds(-codexAge), now, "gpt-5-codex", "api-server",
                Tokens(160_000 + (long)(codexAge * 2_600), claude: false), 0.09 + codexAge * 0.002),
        ];
    }

    // Shaped, deterministic usage: busy weekdays, quiet weekends, working-hours curve; Claude heavy on cache reads.
    private static (List<AgentBucket> Hours, List<AgentBucket> Days) Buckets(DateTimeOffset now)
    {
        var random = new Random(11);
        var today = new DateTimeOffset(now.Date, now.Offset);
        var days = new List<AgentBucket>();
        for (var i = 29; i >= 0; i--)
        {
            var start = today.AddDays(-i);
            var weekend = start.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var factor = (weekend ? 0.3 : 1) * (0.55 + random.NextDouble() * 0.9) * (i == 0 ? Math.Clamp(now.TimeOfDay.TotalHours / 18, 0.15, 1) : 1);
            days.Add(new AgentBucket(start, Split(factor, random)));
        }
        var hours = new List<AgentBucket>();
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset);
        for (var i = 23; i >= 0; i--)
        {
            var start = hour.AddHours(-i);
            var h = start.Hour;
            var curve = h is >= 9 and <= 19 ? 0.5 + 0.5 * Math.Sin((h - 9) / 10.0 * Math.PI) : h is >= 21 or <= 1 ? 0.25 : 0;
            hours.Add(new AgentBucket(start, Split(curve / 9 * (0.6 + random.NextDouble() * 0.8), random)));
        }
        return (hours, days);
    }

    private static IReadOnlyDictionary<AgentProvider, AgentTotals> Split(double factor, Random random)
    {
        var claudeCost = 14.2 * factor;
        var codexCost = 4.6 * factor * (0.6 + random.NextDouble() * 0.8);
        var map = new Dictionary<AgentProvider, AgentTotals>
        {
            [AgentProvider.Claude] = Totals(claudeCost, claude: true),
            [AgentProvider.Codex] = Totals(codexCost, claude: false),
        };
        if (random.NextDouble() < 0.5)
            map[AgentProvider.Copilot] = Totals(0.9 * factor, claude: false);
        if (random.NextDouble() < 0.3)
            map[AgentProvider.OpenCode] = Totals(0.6 * factor, claude: false);
        return map;
    }

    private static AgentTotals Totals(double cost, bool claude)
    {
        var total = (long)(cost * (claude ? 1_650_000 : 820_000));
        return new AgentTotals(Tokens(total, claude), cost, cost * (claude ? 1.9 : 0.7), (int)Math.Round(cost * (claude ? 11 : 7)), 0);
    }

    private static AgentTokens Tokens(long total, bool claude) => claude
        ? new AgentTokens(total * 3 / 100, total * 7 / 100, total * 85 / 100, total * 5 / 100, 0)
        : new AgentTokens(total * 12 / 100, 0, total * 80 / 100, total * 8 / 100, total * 3 / 100);

    private static AgentPeriodUsage Usage(IEnumerable<AgentBucket> buckets)
    {
        var list = buckets.ToList();
        var byProvider = AgentDisplay.Providers.ToDictionary(p => p, p => list.Aggregate(AgentTotals.Zero, (sum, b) => sum + (b.ByProvider.GetValueOrDefault(p) ?? AgentTotals.Zero)));
        var total = byProvider.Values.Aggregate(AgentTotals.Zero, (a, b) => a + b);
        var claude = byProvider[AgentProvider.Claude];
        var codex = byProvider[AgentProvider.Codex];
        AgentShare Share(string name, AgentProvider? provider, AgentTotals of, double part) =>
            new(name, provider, new AgentTotals(Scale(of.Tokens, part), of.Cost * part, of.Savings * part, (int)(of.Requests * part), 0));
        var models = new List<AgentShare>
        {
            Share("claude-opus-4-1-20250805", AgentProvider.Claude, claude, 0.62),
            Share("claude-sonnet-4-5", AgentProvider.Claude, claude, 0.33),
            Share("gpt-5-codex", AgentProvider.Codex, codex, 0.81),
            Share("gpt-5", AgentProvider.Codex, codex, 0.19),
            Share("claude-haiku-4-5", AgentProvider.Claude, claude, 0.05),
        }.OrderByDescending(s => s.Totals.Cost).ToList();
        var projects = new List<AgentShare>
        {
            Share("lintel", null, total, 0.44),
            Share("api-server", AgentProvider.Codex, total, 0.23),
            Share("dotfiles", null, total, 0.13),
            Share("marketing-site", null, total, 0.11),
            Share("ml-notebooks", null, total, 0.09),
        };
        return new AgentPeriodUsage(total, byProvider, models, projects);
    }

    private static AgentTokens Scale(AgentTokens t, double part) =>
        new((long)(t.Input * part), (long)(t.CacheWrite * part), (long)(t.CacheRead * part), (long)(t.Output * part), (long)(t.Reasoning * part));
}
