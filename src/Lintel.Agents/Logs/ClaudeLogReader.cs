// SPDX-License-Identifier: GPL-3.0-or-later
// Portions adapted from vorssaint-utils, Copyright (C) 2026 Vorssaint, GPL-3.0-or-later.
using System.Text.Json;
using Lintel.Core.Agents;

namespace Lintel.Agents.Logs;

/// <summary>
/// Session logs of Claude Code. A prompt opens a turn, a reply keeps it going and records its usage,
/// an interruption, local-command output or turn-ending tool result closes it.
/// </summary>
internal sealed class ClaudeLogReader : ILogReader
{
    public AgentProvider Provider => AgentProvider.Claude;

    public void Parse(ReadOnlyMemory<byte> line, LogState st, DateTimeOffset now, List<LogEntry> o)
    {
        if (LineFilter.Has(line, "\"type\":\"assistant\""u8)) { Assistant(line, st, now, o); return; }
        if (!LineFilter.Has(line, "\"type\":\"user\""u8)) return;

        // A local command prints its output without asking the model anything; an interruption or output
        // arriving while a turn is open closes it. Only the person's own text counts, never a tool result.
        if (LineFilter.Has(line, "[Request interrupted by user"u8) || LineFilter.Has(line, "<local-command-std"u8))
        {
            using var doc = LogJson.TryParse(line);
            JsonElement? root = doc?.RootElement;
            if (root is not null && root.Str("type") == "user" && EndsTurn(root))
            {
                if (root.IsTrue("isSidechain")) return;
                var open = st.TurnOpen;
                st.TurnOpen = false;
                if (open) o.Add(new LogEntry.TurnEnded(null, false, null));
                return;
            }
        }

        // A tool that ends the turn (structured output of a scripted session) gets no reply after its result.
        if (LineFilter.Has(line, "\"toolEndsTurn\":true"u8))
        {
            using var doc = LogJson.TryParse(line);
            JsonElement? root = doc?.RootElement;
            if (root is not null && root.Str("type") == "user" && root.IsTrue("toolEndsTurn") && !root.IsTrue("isSidechain"))
            {
                var open = st.TurnOpen;
                st.TurnOpen = false;
                if (open) o.Add(new LogEntry.TurnEnded(root.Time("timestamp") ?? now, true, null));
                return;
            }
        }

        // /clear resets the context and may have no reply.
        if (LineFilter.Has(line, "<command-name>"u8))
        {
            using var doc = LogJson.TryParse(line);
            JsonElement? root = doc?.RootElement;
            if (root is not null && root.Str("type") == "user" && !root.IsTrue("isMeta") && !root.IsTrue("isSidechain")
                && Texts(root, singleBlock: true).Select(LocalCommandIsClear).FirstOrDefault(c => c is not null) is { } isClear)
            {
                Adopt(root, st);
                if (isClear)
                {
                    var was = st.TurnOpen;
                    st.TurnOpen = false;
                    if (was) o.Add(new LogEntry.TurnEnded(null, false, null));
                }
                return;
            }
        }

        // Tool results arrive inside a turn and can be large: while a turn is open the line only says work goes on.
        if (st.TurnOpen) { o.Add(new LogEntry.TurnActive(null)); return; }

        // A subagent's prompts never open a turn.
        if (LineFilter.Has(line, "\"isSidechain\":true"u8)) return;
        using var d = LogJson.TryParse(line);
        JsonElement? r = d?.RootElement;
        if (r is null || r.Str("type") != "user" || r.IsTrue("isMeta") || r.IsTrue("isSidechain")) return;
        Adopt(r, st);
        st.TurnOpen = true;
        o.Add(new LogEntry.TurnBegan(r.Time("timestamp") ?? now));
    }

    private static void Assistant(ReadOnlyMemory<byte> line, LogState st, DateTimeOffset now, List<LogEntry> o)
    {
        using var doc = LogJson.TryParse(line);
        JsonElement? root = doc?.RootElement;
        if (root is null || root.Str("type") != "assistant" || root.Prop("message") is not { } message) return;
        JsonElement? msg = message;
        Adopt(root, st);
        var date = root.Time("timestamp") ?? now;
        var model = msg.Str("model") ?? "";
        var id = msg.Str("id") ?? "";
        var request = root.Str("requestId") ?? "";
        var entries = new List<LogEntry>();
        if (msg.Prop("usage") is { ValueKind: JsonValueKind.Object } usageEl && model.Length > 0 && !model.StartsWith('<'))
        {
            JsonElement? usage = usageEl;
            st.Model = model;
            var tokens = new AgentTokens(usage.Count("input_tokens"), usage.Count("cache_creation_input_tokens"),
                usage.Count("cache_read_input_tokens"), usage.Count("output_tokens"),
                usage.Prop("output_tokens_details").Count("thinking_tokens"));
            var billable = new Billable(tokens,
                LongCacheWrite: usage.Prop("cache_creation").Count("ephemeral_1h_input_tokens"),
                Fast: usage.Str("speed") == "fast",
                Domestic: usage.Str("inference_geo") == "us",
                WebSearches: (int)Math.Min(int.MaxValue, usage.Prop("server_tool_use").Count("web_search_requests")));
            var key = id.Length == 0 && request.Length == 0
                ? $"claude:{st.Session}:{date.ToUnixTimeMilliseconds()}" : $"claude:{id}:{request}";
            entries.Add(new LogEntry.Usage(key, AgentProvider.Claude, date, model, st.Project, st.Session, 1, billable));
        }

        // A subagent's own ending is not the end of the turn it serves.
        if (root.IsTrue("isSidechain")) { o.AddRange(entries); return; }
        var stop = msg.Str("stop_reason");
        // Bare local output written with the assistant role is neither a reply nor work.
        if (stop is null && request.Length == 0 && (model == "<synthetic>" || (model.Length == 0 && id.Length == 0))
            && Texts(root, singleBlock: true).Any(IsLocalOutput))
        { o.AddRange(entries); return; }

        switch (stop)
        {
            case "end_turn" or "stop_sequence" or "max_tokens" or "refusal":
                // An error written in place of a reply, like a spent limit, stops the turn without finishing it.
                var failed = root.IsTrue("isApiErrorMessage") || model.StartsWith('<');
                if (st.TurnOpen) entries.Add(new LogEntry.TurnEnded(date, !failed, null));
                st.TurnOpen = false;
                break;
            default:
                // Ahead of the usage, so a reply that opens the turn again counts toward it.
                entries.Insert(0, st.TurnOpen ? new LogEntry.TurnActive(date) : new LogEntry.TurnBegan(date));
                st.TurnOpen = true;
                break;
        }
        o.AddRange(entries);
    }

    private static bool EndsTurn(JsonElement? root) =>
        Texts(root, false).Any(t => t.StartsWith("[Request interrupted by user", StringComparison.Ordinal))
        || Texts(root, true).Any(IsLocalOutput);

    /// <summary>True for /clear, /reset, /new; false for other recognised commands; null when not a plain command.</summary>
    private static bool? LocalCommandIsClear(string text)
    {
        if (!text.StartsWith("<command-", StringComparison.Ordinal)) return null;
        var header = text;
        var args = text.IndexOf("<command-args>", StringComparison.Ordinal);
        if (args >= 0)
        {
            if (!text.TrimEnd().EndsWith("</command-args>", StringComparison.Ordinal)) return null;
            header = text[..args];
        }
        var names = header.Split('\n', '\r').Select(s => s.Trim())
            .Where(s => s.Length > 0 && !(s.StartsWith("<command-message>", StringComparison.Ordinal) && s.EndsWith("</command-message>", StringComparison.Ordinal)))
            .ToList();
        if (names.Count != 1) return null;
        return names[0] switch
        {
            "<command-name>/clear</command-name>" or "<command-name>/reset</command-name>" or "<command-name>/new</command-name>" => true,
            "<command-name>/model</command-name>" => false,
            _ => null,
        };
    }

    private static bool IsLocalOutput(string text)
    {
        var t = text.Trim();
        return (t.StartsWith("<local-command-stdout>", StringComparison.Ordinal) && t.EndsWith("</local-command-stdout>", StringComparison.Ordinal))
            || (t.StartsWith("<local-command-stderr>", StringComparison.Ordinal) && t.EndsWith("</local-command-stderr>", StringComparison.Ordinal));
    }

    /// <summary>Direct text only, never inside a tool result. A command occupies the whole message.</summary>
    private static IEnumerable<string> Texts(JsonElement? root, bool singleBlock)
    {
        var content = root.Prop("message").Prop("content");
        if (content is not { } c) yield break;
        if (c.ValueKind == JsonValueKind.String) { yield return c.GetString() ?? ""; yield break; }
        if (c.ValueKind != JsonValueKind.Array || (singleBlock && c.GetArrayLength() != 1)) yield break;
        foreach (var block in c.EnumerateArray())
            if (block.Str("type") == "text" && block.Str("text") is { } text) yield return text;
    }

    private static void Adopt(JsonElement? root, LogState st)
    {
        if (root.Str("sessionId") is { Length: > 0 } s) st.Session = s;
        if (root.Str("cwd") is { Length: > 0 } cwd) st.Project = ProjectName.From(cwd);
    }
}
