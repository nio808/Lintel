// SPDX-License-Identifier: GPL-3.0-or-later
// Portions adapted from vorssaint-utils, Copyright (C) 2026 Vorssaint, GPL-3.0-or-later.
using System.Text.Json;
using Lintel.Core.Agents;

namespace Lintel.Agents.Logs;

/// <summary>
/// GitHub Copilot's append-only event log. Only session context, turn boundaries and cumulative per-model counters
/// (session.shutdown.modelMetrics) are decoded; prompts, replies, reasoning and tool arguments are ignored.
/// </summary>
internal sealed class CopilotLogReader : ILogReader
{
    private static readonly byte[][] Needles = new[]
    {
        "user.message", "assistant.turn_start", "assistant.message", "assistant.turn_end",
        "session.start", "session.context_changed", "session.model_change", "session.shutdown", "abort",
    }.Select(t => System.Text.Encoding.UTF8.GetBytes($"\"type\":\"{t}\"")).ToArray();

    public AgentProvider Provider => AgentProvider.Copilot;

    public void Parse(ReadOnlyMemory<byte> line, LogState st, DateTimeOffset now, List<LogEntry> o)
    {
        if (!Needles.Any(n => LineFilter.Has(line, n))) return;
        using var doc = LogJson.TryParse(line);
        JsonElement? env = doc?.RootElement;
        if (env.Str("type") is not { } type || env.Prop("data") is not { } dataEl) return;
        JsonElement? data = dataEl;
        var agent = env.Str("agentId") is { Length: > 0 } a ? a : null;
        var root = agent is null;
        // Subagents share the root log: their responses count as activity, their lifecycle never changes the root task.
        if (!root && type != "assistant.message") return;
        var date = env.Time("timestamp") ?? now;
        switch (type)
        {
            case "user.message" or "assistant.turn_start":
                st.CopilotFinalResponse = false;
                st.CopilotTurnId = type == "assistant.turn_start" ? data.Str("turnId") : null;
                if (data.Str("model") is { Length: > 0 } m) st.Model = m;
                o.Add(st.TurnOpen ? new LogEntry.TurnActive(date) : new LogEntry.TurnBegan(date));
                st.TurnOpen = true;
                break;
            case "assistant.message":
                var model = data.Str("model") is { Length: > 0 } mm ? mm : (root ? st.Model : "");
                o.Add(Activity(data, env, agent, model, date, st));
                if (!root) break;
                st.Model = model;
                // Each tool iteration has its own turn_end; only a final response can finish the person's task.
                st.CopilotFinalResponse = !HasItems(data.Prop("toolRequests")) && data.Str("phase") is not ("thinking" or "commentary");
                if (st.TurnOpen) o.Add(new LogEntry.TurnActive(date));
                break;
            case "assistant.turn_end":
                if (!st.TurnOpen || (st.CopilotTurnId is not null && st.CopilotTurnId != data.Str("turnId"))) break;
                if (!st.CopilotFinalResponse) { o.Add(new LogEntry.TurnActive(date)); break; }
                st.TurnOpen = false;
                st.CopilotFinalResponse = false;
                o.Add(new LogEntry.TurnEnded(date, true, null));
                break;
            case "session.start":
                if (data.Str("sessionId") is { Length: > 0 } sid) st.Session = sid;
                if (data.Str("selectedModel") is { Length: > 0 } sel) st.Model = sel;
                if (FirstPath(data.Prop("context")) is { } p) st.Project = ProjectName.From(p);
                break;
            case "session.context_changed":
                if (FirstPath(data) is { } cp) st.Project = ProjectName.From(cp);
                break;
            case "session.model_change":
                if (data.Str("newModel") is { Length: > 0 } nm) st.Model = nm;
                break;
            case "abort":
                if (!st.TurnOpen) break;
                st.TurnOpen = false;
                st.CopilotFinalResponse = false;
                o.Add(new LogEntry.TurnEnded(date, false, null));
                break;
            case "session.shutdown":
                Shutdown(data.Prop("modelMetrics"), env.Str("id"), date, st, o);
                if (st.TurnOpen)
                {
                    st.TurnOpen = false;
                    o.Add(new LogEntry.TurnEnded(date, false, null));
                }
                break;
        }
    }

    private static string? FirstPath(JsonElement? e) =>
        new[] { "gitRoot", "cwd", "repository" }.Select(k => e.Str(k)).FirstOrDefault(s => !string.IsNullOrEmpty(s));

    private static bool HasItems(JsonElement? e) => e is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() > 0;

    // one activity record per API call/message (request count, no tokens); shutdown records carry the tokens
    // with Requests=0, so a request is never counted twice. Reconciling the log's own reported request counts is not done.
    private static LogEntry Activity(JsonElement? data, JsonElement? env, string? agent, string model, DateTimeOffset date, LogState st)
    {
        var call = data.Str("apiCallId") is { Length: > 0 } c ? c : null;
        var message = data.Str("messageId") is { Length: > 0 } mid ? mid : env.Str("id") ?? date.ToUnixTimeMilliseconds().ToString();
        var request = (agent is null ? "root" : $"agent:{agent}") + ":" + (call is null ? $"message:{message}" : $"api:{call}");
        return new LogEntry.Usage($"copilot:{st.Session}:{request}:activity", AgentProvider.Copilot, date, model, st.Project, st.Session,
            1, new Billable(default, IsAggregate: true));
    }

    private static void Shutdown(JsonElement? metrics, string? eventId, DateTimeOffset date, LogState st, List<LogEntry> o)
    {
        if (metrics is not { ValueKind: JsonValueKind.Object } m) return;
        foreach (var model in m.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            JsonElement? metric = model.Value;
            var total = Tokens(metric);
            var growth = st.CopilotTotals.TryGetValue(model.Name, out var prev) && total.Total >= prev.Total ? total - prev : total;
            st.CopilotTotals[model.Name] = total;
            if (growth.Total <= 0) continue;
            var checkpoint = string.IsNullOrEmpty(eventId) ? date.ToUnixTimeMilliseconds().ToString() : eventId;
            o.Add(new LogEntry.Usage($"copilot:{st.Session}:{checkpoint}:{model.Name}", AgentProvider.Copilot, date, model.Name,
                st.Project, st.Session, 0, new Billable(growth, IsAggregate: true)));
        }
    }

    private static AgentTokens Tokens(JsonElement? metric)
    {
        var details = metric.Prop("tokenDetails");
        var usage = metric.Prop("usage");
        long Count(string detail, string fallback) =>
            details.Prop(detail).Prop("tokenCount") is { } v ? ((JsonElement?)v).Count() : usage.Count(fallback);
        return new(Count("input", "inputTokens"), Count("cache_write", "cacheWriteTokens"), Count("cache_read", "cacheReadTokens"),
            Count("output", "outputTokens"), usage.Count("reasoningTokens"));
    }
}
