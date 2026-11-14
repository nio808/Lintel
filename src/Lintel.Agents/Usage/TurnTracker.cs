// SPDX-License-Identifier: GPL-3.0-or-later
// Portions adapted from vorssaint-utils, Copyright (C) 2026 Vorssaint, GPL-3.0-or-later.
using Lintel.Core.Agents;

namespace Lintel.Agents.Usage;

/// <summary>
/// Open agent turns ("working" sessions). A turn silent for 10 minutes is no longer shown (and never notifies by that),
/// but is kept so work that resumes after an approval or a long command goes on with it. Finished events need the
/// end observed within 5 minutes of it happening, so history found late (an archived session) is never news.
/// </summary>
internal sealed class TurnTracker
{
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan LateEnd = TimeSpan.FromMinutes(5);

    private sealed class Turn(string id, AgentProvider provider, DateTimeOffset started)
    {
        public string Id { get; } = id;
        public AgentProvider Provider { get; } = provider;
        public DateTimeOffset Started { get; } = started;
        public DateTimeOffset LastActivity { get; set; } = started;
        public string Model { get; set; } = "";
        public string Project { get; set; } = "";
        public AgentTokens Tokens { get; set; }
        public double Cost { get; set; }
        public int Unpriced { get; set; }
    }

    private readonly Dictionary<string, Turn> turns = [];

    /// <summary>Off while the logs are first read, so history never replays as news.</summary>
    public bool ReportsTransitions { get; set; }

    public void Began(string file, AgentProvider provider, DateTimeOffset at)
    {
        // A log rewritten in place is read again from its start: the turn it already holds keeps what it has.
        if (turns.TryGetValue(file, out var t) && Math.Abs((t.Started - at).TotalSeconds) < 1) return;
        turns[file] = new Turn(file, provider, at);
    }

    public void Active(string file, AgentProvider provider, DateTimeOffset? at, DateTimeOffset modified)
    {
        var moment = at ?? modified;
        if (turns.TryGetValue(file, out var t)) t.LastActivity = moment > t.LastActivity ? moment : t.LastActivity;
        else turns[file] = new Turn(file, provider, moment);
    }

    public void Context(string file, string model, string project)
    {
        if (!turns.TryGetValue(file, out var t)) return;
        if (model.Length > 0) t.Model = model;
        if (project.Length > 0) t.Project = project;
    }

    /// <summary>A response counts toward the open turn of <paramref name="file"/> (the log's own, or a subagent's parent).</summary>
    public void AddUsage(string file, DateTimeOffset date, UsageDelta delta, string model, string project, bool subagent)
    {
        if (!turns.TryGetValue(file, out var t) || date < t.Started.AddSeconds(-1)) return;
        t.Tokens += delta.Tokens;
        t.Cost += delta.Cost;
        t.Unpriced += delta.Unpriced;
        if (date > t.LastActivity) t.LastActivity = date;
        // A subagent's responses leave the turn's model and project alone.
        if (!subagent) Context(file, model, project);
    }

    /// <summary>Closes a turn; returns the Finished event when it completed, was seen live and is worth reporting.</summary>
    public AgentUsageEvent.Finished? Ended(string file, DateTimeOffset? at, bool completed, TimeSpan? duration,
                                           DateTimeOffset modified, DateTimeOffset now)
    {
        if (!turns.Remove(file, out var t) || !completed || !ReportsTransitions) return null;
        var end = at ?? modified;
        if (now - end > LateEnd) return null;
        var span = duration ?? end - t.Started;
        return new AgentUsageEvent.Finished(t.Provider, span < TimeSpan.Zero ? TimeSpan.Zero : span,
            t.Unpriced > 0 && t.Cost == 0 ? null : t.Cost, t.Tokens.Total, t.Project);
    }

    /// <summary>A log removed or restarted while its turn ran ends that turn without a notice: nothing finished.</summary>
    public void Forget(string file) => turns.Remove(file);

    /// <summary>Drops turns nobody resumed: an hour for Claude (a session resumed after its process died reads like work going on), six for others.</summary>
    public void Prune(DateTimeOffset now)
    {
        foreach (var id in turns.Where(p => now - p.Value.LastActivity > (p.Value.Provider == AgentProvider.Claude ? TimeSpan.FromHours(1) : TimeSpan.FromHours(6)))
                     .Select(p => p.Key).ToList())
            turns.Remove(id);
    }

    /// <summary>Turns with activity in the last 10 minutes, newest first; ids are opaque (no file paths leave the tracker).</summary>
    public List<AgentLiveSession> Live(DateTimeOffset now) =>
        turns.Values.Where(t => now - t.LastActivity < Idle).OrderByDescending(t => t.LastActivity)
            .Select(t => new AgentLiveSession($"{t.Provider}:{(uint)t.Id.GetHashCode():x8}", t.Provider, t.Started, t.LastActivity, t.Model, t.Project, t.Tokens, t.Cost)).ToList();
}
