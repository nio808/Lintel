// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Core.Agents;

namespace Lintel.Agents.Limits;

/// <summary>
/// Pure-ish: decides when a limit window's remaining percentage deserves a notice. Never on the first reading of a window
/// (thresholds already crossed are armed silently), once per threshold until the window resets, only the lowest threshold
/// when one reading crosses several, and an optional notice when a window that was warned about resets.
/// </summary>
internal sealed class AlertTracker
{
    private sealed class State(double remaining, DateTimeOffset? resetsAt)
    {
        public double Remaining { get; set; } = remaining;
        public DateTimeOffset? ResetsAt { get; set; } = resetsAt;
        public HashSet<double> Fired { get; } = [];
        public bool Warned { get; set; }
    }

    private readonly Dictionary<(string Account, string Window), State> states = [];

    /// <summary>Call with fresh readings only (stale ones say nothing new).</summary>
    public List<AgentUsageEvent> Evaluate(AgentLimits current, IReadOnlyList<double> thresholds, bool warn, bool onReset)
    {
        var events = new List<AgentUsageEvent>();
        if (current.IsStale) return events;
        foreach (var w in current.Windows)
        {
            var remaining = w.RemainingPercent;
            var key = (current.AccountId, w.Id);
            if (!states.TryGetValue(key, out var s))
            {
                s = states[key] = new State(remaining, w.ResetsAt);
                foreach (var t in thresholds.Where(t => remaining < t)) s.Fired.Add(t);   // first reading: never an alert
                continue;
            }

            var reset = remaining > s.Remaining + 1
                        || (w.ResetsAt is { } r && s.ResetsAt is { } before && r > before.AddMinutes(1));
            if (reset)
            {
                if (s.Warned && onReset) events.Add(new AgentUsageEvent.LimitReset(current, w));
                s.Fired.Clear();
                s.Warned = false;
                foreach (var t in thresholds.Where(t => remaining < t)) s.Fired.Add(t);
            }
            else
            {
                var crossed = thresholds.Where(t => remaining < t && !s.Fired.Contains(t)).ToList();
                if (crossed.Count > 0)
                {
                    foreach (var t in crossed) s.Fired.Add(t);
                    if (warn)
                    {
                        events.Add(new AgentUsageEvent.LimitWarning(current, w, crossed.Min()));
                        s.Warned = true;
                    }
                }
            }
            s.Remaining = remaining;
            s.ResetsAt = w.ResetsAt ?? s.ResetsAt;
        }
        return events;
    }

    public void Forget(string accountId)
    {
        foreach (var key in states.Keys.Where(k => k.Account == accountId).ToList()) states.Remove(key);
    }
}
