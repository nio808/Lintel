// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Core.Agents;

namespace Lintel.Agents.Limits;

/// <summary>
/// Schedules limit reads (every 5 min per account, 60 s while the AI section is visible), applies the back-off table,
/// the 30-second manual rule and the last-known semantics, and chooses between the account reading and the fallback
/// (Claude app history / Codex session logs). All reads are serialised (Codex homes are checked one at a time).
/// </summary>
internal sealed class LimitsCoordinator(
    TimeProvider time,
    Func<AgentAccount, CancellationToken, Task<AgentLimits>> readAccount,
    Func<AgentAccount, bool> canReadAccount,
    Func<AgentAccount, AgentLimits?> readFallback)
{
    private sealed class AccountState(AgentAccount account)
    {
        public AgentAccount Account { get; } = account;
        public AgentLimits? LastAccount { get; set; }
        public DateTimeOffset? LastSuccess { get; set; }
        public DateTimeOffset? FailedAt { get; set; }
        public DateTimeOffset NextAttempt { get; set; }
        public int Failures { get; set; }
        public string? Error { get; set; }
        public AgentLimits? Composed { get; set; }
    }

    private readonly object sync = new();
    private readonly Dictionary<string, AccountState> states = [];
    private readonly SemaphoreSlim reading = new(1, 1);

    /// <summary>A composed reading changed (new numbers, staleness, error). Raised outside the lock, on whichever thread caused it.</summary>
    public event Action<AgentLimits>? Changed;

    public IReadOnlyList<AgentLimits> Current
    {
        get
        {
            lock (sync) return states.Values.Select(s => s.Composed).OfType<AgentLimits>().ToList();
        }
    }

    public void SetAccounts(IReadOnlyList<AgentAccount> accounts)
    {
        lock (sync)
        {
            foreach (var gone in states.Keys.Where(k => accounts.All(a => a.Id != k)).ToList()) states.Remove(gone);
            foreach (var a in accounts)
                if (!states.TryGetValue(a.Id, out var s)) states[a.Id] = new AccountState(a) { NextAttempt = time.GetUtcNow() };
                else if (s.Account != a) states[a.Id] = new AccountState(a) { NextAttempt = time.GetUtcNow(), LastAccount = s.LastAccount, LastSuccess = s.LastSuccess };
        }
    }

    /// <summary>Reads every due account, one at a time.</summary>
    public async Task TickAsync(bool sectionVisible, CancellationToken ct)
    {
        List<AccountState> due;
        lock (sync)
        {
            var now = time.GetUtcNow();
            due = states.Values.Where(s => IsDue(s, now, sectionVisible)).ToList();
        }
        foreach (var s in due)
        {
            ct.ThrowIfCancellationRequested();
            await CheckAsync(s, ct).ConfigureAwait(false);
        }
    }

    private bool IsDue(AccountState s, DateTimeOffset now, bool visible)
    {
        if (!canReadAccount(s.Account)) return false;
        if (now >= s.NextAttempt) return true;
        // A visible section may refresh a reading older than 60 s, but never inside a failure back-off.
        return visible && s.Failures == 0 && (s.LastSuccess is null || now - s.LastSuccess >= BackoffPolicy.VisibleFreshness);
    }

    /// <summary>
    /// User-initiated refresh. At most one successful read per 30 s per account; back-off is respected.
    /// Returns true when a read was attempted.
    /// </summary>
    public async Task<bool> RefreshAsync(string? accountId, CancellationToken ct)
    {
        List<AccountState> targets;
        lock (sync) targets = states.Values.Where(s => accountId is null || s.Account.Id == accountId).ToList();
        var attempted = false;
        foreach (var s in targets)
        {
            bool allowed;
            lock (sync)
            {
                var now = time.GetUtcNow();
                allowed = canReadAccount(s.Account)
                          && !(s.Failures > 0 && now < s.NextAttempt)
                          && !(s.LastSuccess is { } ok && now - ok < BackoffPolicy.ManualMinimum);
            }
            if (allowed) { await CheckAsync(s, ct).ConfigureAwait(false); attempted = true; }
            else Recompose(s);
        }
        return attempted;
    }

    /// <summary>The fallback sources changed (new log lines, new app history sample): choose again without any network.</summary>
    public void Recompose()
    {
        List<AccountState> all;
        lock (sync) all = states.Values.ToList();
        foreach (var s in all) Recompose(s);
    }

    private async Task CheckAsync(AccountState s, CancellationToken ct)
    {
        await reading.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AgentLimits? ok = null;
            Exception? error = null;
            try { ok = await readAccount(s.Account, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { error = e; }

            lock (sync)
            {
                var now = time.GetUtcNow();
                if (ok is not null)
                {
                    s.LastAccount = ok with { ObservedAt = now };
                    s.LastSuccess = now;
                    s.Failures = 0;
                    s.Error = null;
                    s.NextAttempt = now + BackoffPolicy.Interval;
                }
                else
                {
                    s.Failures++;
                    s.FailedAt = now;
                    s.Error = BackoffPolicy.Friendly(error!);
                    s.NextAttempt = now + BackoffPolicy.Delay(BackoffPolicy.KindOf(error!), s.Failures, (error as LimitReadException)?.RetryAfter);
                }
            }
        }
        finally { reading.Release(); }
        Recompose(s);
    }

    private void Recompose(AccountState s)
    {
        AgentLimits? previous, next;
        lock (sync)
        {
            if (!states.ContainsKey(s.Account.Id)) return;
            previous = s.Composed;
            next = s.Composed = Compose(s, time.GetUtcNow());
        }
        if (next is not null && !Same(previous, next)) Changed?.Invoke(next);
    }

    /// <summary>Chooses what to show for one account (called under the lock).</summary>
    private AgentLimits? Compose(AccountState s, DateTimeOffset now)
    {
        var a = s.LastAccount;
        var f = readFallback(s.Account);
        var failing = s.Failures > 0;
        var canRead = canReadAccount(s.Account);
        AgentLimits? chosen = null;
        var stale = false;
        string? note = null;

        if (s.Account.Provider == AgentProvider.Claude)
        {
            // Account reading if newer than the newest app-history sample, else app history while fresh, else the stale account reading.
            if (a is not null && (f is null || a.ObservedAt >= f.ObservedAt)) { chosen = a; stale = failing; }
            else if (f is not null && now - f.ObservedAt < Limits.ClaudeAppHistory.Freshness) chosen = f with { Plan = a?.Plan };
            else if (a is not null) { chosen = a; stale = true; }
            else if (f is not null)
            {
                chosen = f;
                stale = true;
                note = "Claude app history is out of date. Open the Claude app, or sign in with `claude auth login`.";
            }
        }
        else
        {
            // Codex: newer of the account reading and the one copied into session logs.
            chosen = a is null ? f : f is null ? a : f.ObservedAt > a.ObservedAt ? f : a;
            stale = failing && chosen is not null && !(ReferenceEquals(chosen, f) && f is not null && f.ObservedAt >= (s.FailedAt ?? DateTimeOffset.MinValue));
            if (chosen is not null && ReferenceEquals(chosen, f) && f is not null) chosen = f with { Plan = f.Plan ?? a?.Plan };
        }

        var next = canRead ? s.NextAttempt : (DateTimeOffset?)null;
        if (chosen is null)
        {
            if (!canRead || s.Error is null) return null;
            return new AgentLimits(s.Account.Id, s.Account.Provider, [], s.FailedAt ?? now, LimitSource.Account, null, null, false, true, s.Error, next);
        }
        return chosen with
        {
            AccountId = s.Account.Id,
            IsStale = stale,
            Error = stale ? s.Error ?? note ?? "This reading is out of date." : null,
            NextAttempt = next,
        };
    }

    internal static bool Same(AgentLimits? a, AgentLimits? b) =>
        a is not null && b is not null
        && a.ObservedAt == b.ObservedAt && a.Source == b.Source && a.IsStale == b.IsStale && a.Error == b.Error
        && a.NextAttempt == b.NextAttempt && a.Blocked == b.Blocked && a.Credits == b.Credits && a.Plan == b.Plan
        && a.Windows.SequenceEqual(b.Windows);
}
