// SPDX-License-Identifier: GPL-3.0-or-later
// Portions adapted from vorssaint-utils, Copyright (C) 2026 Vorssaint, GPL-3.0-or-later.
using System.Text.Json;
using Lintel.Core.Agents;

namespace Lintel.Agents.Logs;

/// <summary>
/// Codex session logs. Token records and running-total events become usage, task events open and close turns,
/// and limit readings (token_count.rate_limits) become the account's windows with source SessionLog.
/// </summary>
internal sealed class CodexLogReader : ILogReader
{
    private static readonly HashSet<string> Events =
        ["token_count", "task_started", "task_complete", "turn_aborted", "thread_settings_applied"];

    public AgentProvider Provider => AgentProvider.Codex;

    public void Parse(ReadOnlyMemory<byte> line, LogState st, DateTimeOffset now, List<LogEntry> o)
    {
        if (LineFilter.FirstType(line.Span) is not { } kind) return;
        switch (kind.Name)
        {
            case "token_usage_record" or "turn_context" or "session_meta": break;
            case "event_msg":
                if (LineFilter.FirstType(line.Span, kind.End) is not { } ev || !Events.Contains(ev.Name)) return;
                break;
            default: return;
        }
        using var doc = LogJson.TryParse(line);
        JsonElement? root = doc?.RootElement;
        if (root.Prop("payload") is not { } payloadEl) return;
        JsonElement? payload = payloadEl;
        var date = root.Time("timestamp") ?? now;
        switch (root.Str("type"))
        {
            case "session_meta":
                // History copied from another thread repeats that thread's metadata; only the first line is this log's own.
                var id = payload.Str("id") ?? "";
                if (id.Length > 0) st.CodexTotalsSession = id;
                if (st.CodexHeaderRead) return;
                st.CodexHeaderRead = true;
                if (id.Length > 0) st.Session = id;
                if (payload.Str("cwd") is { Length: > 0 } cwd) st.Project = ProjectName.From(cwd);
                // Subagents, reviews and memory tasks Codex starts itself name "subagent" or "internal".
                var source = payload.Prop("source");
                st.CodexSubagent = source.Prop("subagent") is not null || source.Prop("internal") is not null;
                var rootId = payload.Str("session_id") ?? payload.Str("parent_thread_id") ?? "";
                if (st.CodexSubagent && rootId.Length > 0 && rootId != st.Session) st.CodexRoot = rootId;
                return;
            case "turn_context":
                if (payload.Str("model") is { Length: > 0 } model) st.Model = model;
                if (payload.Str("cwd") is { Length: > 0 } c) st.Project = ProjectName.From(c);
                if (payload.Str("service_tier") is { } tier) st.Fast = LogJson.FastTier(tier);
                return;
            case "token_usage_record":
                if (payload.Prop("usage") is not { } usage) return;
                st.SawUsageRecords = true;
                if (st.Session.Length == 0 && payload.Str("session_id") is { Length: > 0 } sid) st.Session = sid;
                var response = payload.Str("response_id") ?? "";
                var key = response.Length == 0 ? $"codex:{st.Session}:{date.ToUnixTimeMilliseconds()}" : $"codex:{response}";
                o.Add(Usage(LogJson.CodexTokens(usage), key, date, st));
                return;
            case "event_msg":
                Event(payload, date, st, o);
                return;
        }
    }

    private static void Event(JsonElement? p, DateTimeOffset date, LogState st, List<LogEntry> o)
    {
        switch (p.Str("type"))
        {
            case "token_count":
                if (p.Prop("rate_limits") is { } limitsEl)
                {
                    JsonElement? limits = limitsEl;
                    if (IsMainBucket(limits) && CodexWindows(limits, date).ToList() is { Count: > 0 } windows)
                        o.Add(new LogEntry.Limits(windows, date));
                    if (limits.Str("plan_type") is { Length: > 0 } plan) o.Add(new LogEntry.Plan(plan, date));
                }
                // Running totals repeat when only the limits changed; a response is what the total grew by.
                if (!st.SawUsageRecords && p.Prop("info") is { } infoEl)
                {
                    JsonElement? info = infoEl;
                    if (info.Prop("total_token_usage") is not { } totalEl) return;
                    var total = LogJson.CodexTokens(totalEl);
                    var last = st.LastTotal;
                    if (total != last && total.Total > (last?.Total ?? 0))
                    {
                        var delta = info.Prop("last_token_usage") is { } lastEl ? LogJson.CodexTokens(lastEl) : total - (last ?? default);
                        o.Add(Usage(delta, $"codex:{st.CodexTotalsSession}:total:{total.Total}", date, st));
                    }
                    st.LastTotal = total;
                }
                return;
            case "thread_settings_applied":
                if (p.Prop("thread_settings").Str("service_tier") is { } tier) st.Fast = LogJson.FastTier(tier);
                return;
            case "task_started":
                st.TurnOpen = true;
                o.Add(new LogEntry.TurnBegan(LogJson.Seconds(p.Dbl("started_at")) ?? date));
                return;
            case "task_complete" or "turn_aborted":
                var open = st.TurnOpen;
                st.TurnOpen = false;
                if (!open) return;
                TimeSpan? duration = p.Dbl("duration_ms") is { } ms ? TimeSpan.FromMilliseconds(ms) : null;
                o.Add(new LogEntry.TurnEnded(LogJson.Seconds(p.Dbl("completed_at")) ?? date, p.Str("type") == "task_complete", duration));
                return;
        }
    }

    private static LogEntry Usage(AgentTokens tokens, string key, DateTimeOffset date, LogState st) =>
        new LogEntry.Usage(key, AgentProvider.Codex, date, st.Model, st.Project, st.Session, 1, new Billable(tokens, Fast: st.Fast));

    /// <summary>Codex logs a reading per allowance; only the main one ("codex", or unnamed in older logs) is kept.</summary>
    private static bool IsMainBucket(JsonElement? limits) =>
        (limits.Str("limit_id") ?? "").ToLowerInvariant() is "" or "codex";

    /// <summary>
    /// Windows are told apart by length, never by slot: an account can report only its weekly window, in either slot.
    /// Business accounts can leave both empty and report the share left of their own limit instead.
    /// </summary>
    public static IEnumerable<AgentLimitWindow> CodexWindows(JsonElement? limits, DateTimeOffset observed)
    {
        var any = false;
        foreach (var slot in new[] { "primary", "secondary" })
        {
            var w = limits.Prop(slot);
            if (w.Dbl("used_percent") is not { } used) continue;
            int? minutes = w.Dbl("window_minutes") is { } m ? (int)m : null;
            var resets = w.Time("resets_at");
            if (resets is null && w.Dbl("resets_in_seconds") is { } delay) resets = observed.AddSeconds(Math.Max(0, delay));
            any = true;
            yield return new AgentLimitWindow($"codex.{(minutes?.ToString() ?? slot)}", LogJson.Kind(minutes), minutes, null,
                Lintel.Agents.Limits.CodexUsageParser.WindowLabel(minutes), Math.Clamp(used, 0, 100), resets);
        }
        if (!any && limits.Prop("individual_limit") is { } indiv)
        {
            JsonElement? ind = indiv;
            if (ind.Dbl("remaining_percent") is { } rem)
                yield return new AgentLimitWindow("codex.individual", LimitKind.Other, null, null, "Quota",
                    Math.Clamp(100 - rem, 0, 100), ind.Time("resets_at"));
        }
    }
}
