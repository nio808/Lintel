// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;

namespace Lintel.Core.Agents;

public enum AgentProvider { Claude, Codex, Copilot, OpenCode }

public enum LimitKind { Session, Weekly, Other }       // ≤ 12 h = Session; 6–8 days = Weekly

public enum LimitSource { Account, SessionLog, ClaudeApp }

public enum AgentPeriod { Today, Week, Month }         // local midnight; last 7 days; last 30 days

public readonly record struct AgentTokens(long Input, long CacheWrite, long CacheRead, long Output, long Reasoning)
{
    public long Total => Input + CacheWrite + CacheRead + Output;      // Output already includes Reasoning
    public long Prompt => Input + CacheWrite + CacheRead;
    public double? CacheHitRate => Prompt > 0 ? (double)CacheRead / Prompt : null;

    public static AgentTokens operator +(AgentTokens a, AgentTokens b) =>
        new(a.Input + b.Input, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead, a.Output + b.Output, a.Reasoning + b.Reasoning);

    /// <summary>Clamped at 0 per field.</summary>
    public static AgentTokens operator -(AgentTokens a, AgentTokens b) =>
        new(Math.Max(0, a.Input - b.Input), Math.Max(0, a.CacheWrite - b.CacheWrite), Math.Max(0, a.CacheRead - b.CacheRead),
            Math.Max(0, a.Output - b.Output), Math.Max(0, a.Reasoning - b.Reasoning));

    /// <summary>Per-field maximum.</summary>
    public AgentTokens Max(AgentTokens other) =>
        new(Math.Max(Input, other.Input), Math.Max(CacheWrite, other.CacheWrite), Math.Max(CacheRead, other.CacheRead),
            Math.Max(Output, other.Output), Math.Max(Reasoning, other.Reasoning));
}

public sealed record AgentPlan(string Name, double? MonthlyPrice);

/// <summary>Ids: "claude:default", "codex:default", "codex:&lt;slug&gt;".</summary>
public sealed record AgentAccount(string Id, AgentProvider Provider, string DisplayName, string? HomePath, bool IsDefault);

public sealed record AgentLimitWindow(string Id, LimitKind Kind, int? Minutes, string? Scope, string Label,
                                      double UsedPercent, DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);

    public bool ResetDue(DateTimeOffset now) => ResetsAt is { } reset && reset <= now;
}

public sealed record AgentLimits(
    string AccountId, AgentProvider Provider, IReadOnlyList<AgentLimitWindow> Windows,
    DateTimeOffset ObservedAt, LimitSource Source,
    AgentPlan? Plan, string? Credits, bool Blocked,
    bool IsStale,            // last attempt failed; Windows are "last known"
    string? Error,           // friendly text, null when fine
    DateTimeOffset? NextAttempt);

public sealed record AgentTotals(AgentTokens Tokens, double Cost, double Savings, int Requests, int Unpriced)
{
    public static AgentTotals Zero { get; } = new(default, 0, 0, 0, 0);

    public static AgentTotals operator +(AgentTotals a, AgentTotals b) =>
        new(a.Tokens + b.Tokens, a.Cost + b.Cost, a.Savings + b.Savings, a.Requests + b.Requests, a.Unpriced + b.Unpriced);
}

public sealed record AgentShare(string Name, AgentProvider? Provider, AgentTotals Totals);

/// <summary>Shares sorted by cost desc.</summary>
public sealed record AgentPeriodUsage(AgentTotals Total, IReadOnlyDictionary<AgentProvider, AgentTotals> ByProvider,
                                      IReadOnlyList<AgentShare> Models, IReadOnlyList<AgentShare> Projects);

public sealed record AgentBucket(DateTimeOffset Start, IReadOnlyDictionary<AgentProvider, AgentTotals> ByProvider);

public sealed record AgentLiveSession(string Id, AgentProvider Provider, DateTimeOffset Started, DateTimeOffset LastActivity,
                                      string Model, string Project, AgentTokens Tokens, double Cost);

public sealed record AgentUsageSnapshot
{
    private static readonly AgentPeriodUsage NoUsage =
        new(AgentTotals.Zero, ReadOnlyDictionary<AgentProvider, AgentTotals>.Empty, [], []);

    /// <summary>Not loaded, no accounts; every period present with zero usage.</summary>
    public static AgentUsageSnapshot Empty { get; } = new();

    public bool Loaded { get; init; }                                  // first index pass done
    public DateTimeOffset Now { get; init; }
    public IReadOnlyList<AgentAccount> Accounts { get; init; } = [];
    public IReadOnlyList<AgentLimits> Limits { get; init; } = [];      // ≤ 1 per account

    /// <summary>Defaults to all three periods with zero usage, so indexing never throws.</summary>
    public IReadOnlyDictionary<AgentPeriod, AgentPeriodUsage> Periods { get; init; } =
        new ReadOnlyDictionary<AgentPeriod, AgentPeriodUsage>(new Dictionary<AgentPeriod, AgentPeriodUsage>
        {
            [AgentPeriod.Today] = NoUsage,
            [AgentPeriod.Week] = NoUsage,
            [AgentPeriod.Month] = NoUsage,
        });

    public IReadOnlyList<AgentBucket> Days { get; init; } = [];        // 30 local days, oldest first, gaps filled
    public IReadOnlyList<AgentBucket> Hours { get; init; } = [];       // last 24 hours
    public IReadOnlyList<AgentLiveSession> Live { get; init; } = [];
    public IReadOnlyList<AgentProvider> Seen { get; init; } = [];      // providers with any data
    public DateOnly? PricesUpdated { get; init; }
}

public abstract record AgentUsageEvent
{
    public sealed record Finished(AgentProvider Provider, TimeSpan Duration, double? Cost, long Tokens, string Project) : AgentUsageEvent;

    public sealed record LimitWarning(AgentLimits Limits, AgentLimitWindow Window, double ThresholdRemaining) : AgentUsageEvent;

    public sealed record LimitReset(AgentLimits Limits, AgentLimitWindow Window) : AgentUsageEvent;
}

public interface IAgentUsageService : IDisposable
{
    AgentUsageSnapshot Current { get; }
    event EventHandler<AgentUsageSnapshot>? SnapshotChanged;   // UI thread, coalesced to ≤ 1 per second
    event EventHandler<AgentUsageEvent>? EventRaised;          // UI thread; the service decides *when*, the UI *how it looks*
    void Start();
    void Stop();                                               // kills any child process
    void SetSectionVisible(bool visible);                      // enables the 60 s freshness rule
    Task RefreshLimitsAsync(string? accountId = null);         // user-initiated; honours back-off and the 30 s rule
    AgentAccount AddCodexHome(string folder, string displayName);
    void RemoveAccount(string accountId);
}
