// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Agents.Logs;
using Lintel.Core.Agents;

namespace Lintel.Agents.Tests;

public sealed class ClaudeLogReaderTests
{
    private static readonly ClaudeLogReader Reader = new();

    private static string User(string text, string extra = "") =>
        $$"""{"type":"user","sessionId":"s","cwd":"/home/u/proj","timestamp":"2026-11-15T10:00:00Z"{{extra}},"message":{"role":"user","content":{{text}}} }""";

    private static string Assistant(string stop, string extra = "", string model = "claude-opus-5-5", string id = "m1", string req = "r1") =>
        $$"""{"type":"assistant","sessionId":"s","cwd":"/home/u/proj","requestId":"{{req}}","timestamp":"2026-11-15T10:00:30Z"{{extra}},"message":{"id":"{{id}}","model":"{{model}}","stop_reason":{{stop}},"usage":{"input_tokens":1,"output_tokens":2},"content":[]} }""";

    [Fact]
    public void FixtureProducesTurnAndUsageEntries()
    {
        var state = new LogState();
        var entries = T.ParseFixture(Reader, "claude-session.jsonl", state);

        var usages = entries.OfType<LogEntry.Usage>().ToList();
        Assert.Equal(["claude:msg1:req1", "claude:msg1:req1", "claude:msg-sub:req-sub", "claude:msg2:req2"], usages.Select(u => u.Key));
        Assert.All(usages, u => Assert.Equal("demo-app", u.Project));
        Assert.All(usages, u => Assert.Equal("sess-claude-1", u.Session));

        // Streamed duplicates repeat the key with growing output counts (the store keeps the maximum).
        Assert.Equal(5, usages[0].Billable.Tokens.Output);
        Assert.Equal(120, usages[1].Billable.Tokens.Output);
        Assert.Equal(400, usages[1].Billable.LongCacheWrite);
        Assert.Equal(new AgentTokens(10, 1000, 2000, 120, 0), usages[1].Billable.Tokens);

        var last = usages[3].Billable;
        Assert.True(last.Fast);
        Assert.True(last.Domestic);
        Assert.Equal(2, last.WebSearches);

        // A prompt opens the turn; tool use keeps it going; the main end_turn completes it; the sidechain's end_turn does not.
        var turns = entries.Where(e => e is not LogEntry.Usage).ToList();
        Assert.IsType<LogEntry.TurnBegan>(turns[0]);
        Assert.IsType<LogEntry.TurnActive>(turns[1]);   // first streamed chunk, stop_reason null
        Assert.IsType<LogEntry.TurnActive>(turns[2]);   // tool_use
        Assert.IsType<LogEntry.TurnActive>(turns[3]);   // tool result while open
        var ended = Assert.IsType<LogEntry.TurnEnded>(Assert.Single(turns.OfType<LogEntry.TurnEnded>()));
        Assert.True(ended.Completed);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 10, 2, 10, TimeSpan.Zero), ended.At);
        Assert.False(state.TurnOpen);
    }

    [Fact]
    public void ProjectIsTheFolderNameOnly()
    {
        var state = new LogState();
        T.ParseFixture(Reader, "claude-session.jsonl", state);
        Assert.Equal("demo-app", state.Project);
        Assert.DoesNotContain("work", state.Project);
    }

    [Fact]
    public void UserInterruptionClosesAnOpenTurnWithoutCompletion()
    {
        var state = new LogState();
        var e = T.Parse(Reader, state, [User("\"fix it\""), Assistant("null"), User("[{\"type\":\"text\",\"text\":\"[Request interrupted by user]\"}]")]);
        var ended = Assert.IsType<LogEntry.TurnEnded>(e.Last());
        Assert.False(ended.Completed);
        Assert.False(state.TurnOpen);
    }

    [Fact]
    public void InterruptionQuotedInsideAToolResultDoesNotEndTheTurn()
    {
        var state = new LogState();
        var toolResult = "[{\"type\":\"tool_result\",\"tool_use_id\":\"x\",\"content\":\"[Request interrupted by user]\"}]";
        var e = T.Parse(Reader, state, [User("\"go\""), User(toolResult)]);
        Assert.True(state.TurnOpen);
        Assert.IsType<LogEntry.TurnActive>(e.Last());
    }

    [Fact]
    public void SidechainRepliesCountTokensButNeverOpenOrCloseTheTurn()
    {
        var state = new LogState();
        var e = T.Parse(Reader, state, [Assistant("\"end_turn\"", ",\"isSidechain\":true", id: "sub", req: "rs")]);
        Assert.IsType<LogEntry.Usage>(Assert.Single(e));
        Assert.False(state.TurnOpen);

        state = new LogState();
        Assert.Empty(T.Parse(Reader, state, [User("\"sub prompt\"", ",\"isSidechain\":true")]));
        Assert.False(state.TurnOpen);
    }

    [Fact]
    public void ClearEndsTheTurnWithoutNotice()
    {
        var state = new LogState();
        var clear = User("\"<command-name>/clear</command-name>\\n            <command-message>clear</command-message>\\n            <command-args></command-args>\"");
        var e = T.Parse(Reader, state, [User("\"go\""), clear]);
        var ended = Assert.IsType<LogEntry.TurnEnded>(e.Last());
        Assert.False(ended.Completed);
        Assert.False(state.TurnOpen);
    }

    [Fact]
    public void ToolEndsTurnCompletesTheTurn()
    {
        var state = new LogState();
        var e = T.Parse(Reader, state, [User("\"go\""), User("[{\"type\":\"tool_result\"}]", ",\"toolEndsTurn\":true")]);
        var ended = Assert.IsType<LogEntry.TurnEnded>(e.Last());
        Assert.True(ended.Completed);
    }

    [Fact]
    public void ApiErrorMessageEndsWithoutCompleting()
    {
        var state = new LogState();
        var e = T.Parse(Reader, state, [User("\"go\""), Assistant("\"end_turn\"", ",\"isApiErrorMessage\":true", model: "<synthetic>")]);
        var ended = Assert.IsType<LogEntry.TurnEnded>(e.Last());
        Assert.False(ended.Completed);
        Assert.DoesNotContain(e, x => x is LogEntry.Usage);   // synthetic models carry no usage
    }

    [Fact]
    public void LocalCommandOutputWhileOpenClosesTheTurn()
    {
        var state = new LogState();
        var e = T.Parse(Reader, state, [User("\"go\""), User("\"<local-command-stdout>ok</local-command-stdout>\"")]);
        Assert.False(Assert.IsType<LogEntry.TurnEnded>(e.Last()).Completed);
    }

    [Fact]
    public void MetaAndUnrelatedLinesAreIgnoredAndGarbageNeverThrows()
    {
        var state = new LogState();
        var e = T.Parse(Reader, state, [User("\"x\"", ",\"isMeta\":true"), "{\"type\":\"summary\",\"summary\":\"s\"}", "not json at all", "{\"type\":\"user\"", "{}", ""]);
        Assert.Empty(e);
    }

    [Fact]
    public void MissingIdsFallBackToASessionAndTimeKey()
    {
        var state = new LogState();
        var line = """{"type":"assistant","sessionId":"s","timestamp":"2026-11-15T10:00:00Z","message":{"model":"claude-opus-5-5","stop_reason":"end_turn","usage":{"output_tokens":3}}}""";
        var u = Assert.IsType<LogEntry.Usage>(T.Parse(Reader, state, [line])[0]);
        Assert.StartsWith("claude:s:", u.Key);
    }
}

public sealed class CodexLogReaderTests
{
    private static readonly CodexLogReader Reader = new();

    [Fact]
    public void RecordFormatUsesTokenUsageRecordsAndSplitsCachedInput()
    {
        var state = new LogState();
        var e = T.ParseFixture(Reader, "codex-session.jsonl", state);

        var usages = e.OfType<LogEntry.Usage>().ToList();
        Assert.Equal(2, usages.Count);
        Assert.All(usages, u => Assert.Equal("codex:r1", u.Key));
        // input_tokens (1000) includes cached (600): Input = 400, CacheRead = 600.
        Assert.Equal(new AgentTokens(400, 0, 600, 50, 20), usages[0].Billable.Tokens);
        Assert.Equal("gpt-6.1-sol", usages[0].Model);
        Assert.Equal("codex-proj", usages[0].Project);
        Assert.Equal("sess-root", usages[0].Session);
        Assert.True(usages[0].Billable.Fast);   // service_tier: fast
        Assert.True(state.SawUsageRecords);
    }

    [Fact]
    public void LimitsInTokenCountBecomeSessionLogWindowsMainBucketOnly()
    {
        var e = T.ParseFixture(Reader, "codex-session.jsonl");
        var limits = Assert.Single(e.OfType<LogEntry.Limits>());   // codex_other bucket is ignored
        Assert.Equal(["codex.300", "codex.10080"], limits.Windows.Select(w => w.Id));
        Assert.Equal([LimitKind.Session, LimitKind.Weekly], limits.Windows.Select(w => w.Kind));
        Assert.Equal(15, limits.Windows[0].UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794232800), limits.Windows[0].ResetsAt);
        // resets_in_seconds is relative to the line's time.
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 10, 0, 11, TimeSpan.Zero).AddDays(1), limits.Windows[1].ResetsAt);
        var plan = Assert.Single(e.OfType<LogEntry.Plan>());
        Assert.Equal("plus", plan.Name);
    }

    [Fact]
    public void TaskLifecycleOpensAndClosesTheTurn()
    {
        var state = new LogState();
        var e = T.ParseFixture(Reader, "codex-session.jsonl", state);
        var began = Assert.Single(e.OfType<LogEntry.TurnBegan>());
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 10, 0, 2, TimeSpan.Zero), began.At);
        var ended = Assert.Single(e.OfType<LogEntry.TurnEnded>());
        Assert.True(ended.Completed);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 10, 2, 0, TimeSpan.Zero), ended.At);
    }

    [Fact]
    public void AbortedTurnIsNotCompletedAndCloseWithoutOpenIsIgnored()
    {
        var state = new LogState();
        var e = T.Parse(Reader, state, [
            """{"timestamp":"2026-11-15T10:00:00Z","type":"event_msg","payload":{"type":"task_complete"}}""",
            """{"timestamp":"2026-11-15T10:00:01Z","type":"event_msg","payload":{"type":"task_started"}}""",
            """{"timestamp":"2026-11-15T10:00:30Z","type":"event_msg","payload":{"type":"turn_aborted"}}"""]);
        Assert.False(Assert.IsType<LogEntry.TurnEnded>(e.Last()).Completed);
        Assert.Equal(2, e.Count);
    }

    [Fact]
    public void RunningTotalsBecomeDeltasAndRepeatsAreSkipped()
    {
        var state = new LogState();
        var e = T.ParseFixture(Reader, "codex-totals.jsonl", state);
        var usages = e.OfType<LogEntry.Usage>().ToList();
        Assert.Equal(2, usages.Count);   // the repeated total (limits-only change) produced nothing
        Assert.Equal(new AgentTokens(400, 0, 100, 40, 0), usages[0].Billable.Tokens);
        // Second step: total went 500/100/40 -> 900/300/100; no last_token_usage so the delta is the difference.
        Assert.Equal(new AgentTokens(200, 0, 200, 60, 0), usages[1].Billable.Tokens);
        Assert.Equal("legacy", usages[0].Project);   // POSIX path
        Assert.NotEqual(usages[0].Key, usages[1].Key);
    }

    [Fact]
    public void LastTokenUsageIsPreferredOverTheDifferenceWhenPresent()
    {
        var state = new LogState { Session = "s" };
        var line = """{"timestamp":"2026-11-15T10:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":900,"output_tokens":100},"last_token_usage":{"input_tokens":300,"cached_input_tokens":100,"output_tokens":20}}}}""";
        var u = Assert.IsType<LogEntry.Usage>(T.Parse(Reader, state, [line]).Single());
        Assert.Equal(new AgentTokens(200, 0, 100, 20, 0), u.Billable.Tokens);
    }

    [Fact]
    public void SubagentHeaderMarksTheFileAndOnlyTheFirstMetadataLineCounts()
    {
        var state = new LogState();
        var e = T.ParseFixture(Reader, "codex-subagent.jsonl", state);
        Assert.True(state.CodexSubagent);
        Assert.Equal("sess-sub", state.Session);
        Assert.Equal("sess-root", state.CodexRoot);
        Assert.Equal("codex-proj", state.Project);        // the copied header of another thread did not override it
        Assert.Equal("sess-copied", state.CodexTotalsSession);
        Assert.Single(e.OfType<LogEntry.Usage>());
    }

    [Fact]
    public void IrrelevantLinesAreSkippedWithoutParsing()
    {
        var state = new LogState();
        var e = T.Parse(Reader, state, [
            """{"timestamp":"2026-11-15T10:00:00Z","type":"response_item","payload":{"type":"message","content":"{\"type\":\"token_usage_record\"}"}}""",
            """{"timestamp":"2026-11-15T10:00:00Z","type":"event_msg","payload":{"type":"agent_message","message":"hi"}}""",
            "garbage"]);
        Assert.Empty(e);
    }

    [Theory]
    [InlineData(300, LimitKind.Session)]
    [InlineData(720, LimitKind.Session)]
    [InlineData(721, LimitKind.Other)]
    [InlineData(10080, LimitKind.Weekly)]
    [InlineData(8640, LimitKind.Weekly)]
    [InlineData(2880, LimitKind.Other)]
    [InlineData(null, LimitKind.Other)]
    public void LimitKindFromMinutes(int? minutes, LimitKind kind) => Assert.Equal(kind, LogJson.Kind(minutes));

    [Fact]
    public void BusinessAccountsMayReportOnlyAShareLeftOfTheirOwnLimit()
    {
        var line = """{"timestamp":"2026-11-15T10:00:00Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"individual_limit":{"remaining_percent":65,"resets_at":1794232800}}}}""";
        var limits = Assert.IsType<LogEntry.Limits>(T.Parse(Reader, new LogState(), [line]).Single());
        var w = Assert.Single(limits.Windows);
        Assert.Equal(35, w.UsedPercent);
        Assert.Equal("codex.individual", w.Id);
    }
}

public sealed class CopilotLogReaderTests
{
    private static readonly CopilotLogReader Reader = new();

    [Fact]
    public void ActivityRecordsCarryRequestsAndShutdownCarriesTokens()
    {
        var state = new LogState();
        var e = T.ParseFixture(Reader, "copilot-events.jsonl", state);
        var usages = e.OfType<LogEntry.Usage>().ToList();

        var activity = usages.Where(u => u.Key.EndsWith(":activity")).ToList();
        Assert.Equal(2, activity.Count);
        Assert.All(activity, u => Assert.Equal(1, u.Requests));
        Assert.All(activity, u => Assert.Equal(0, u.Billable.Tokens.Total));
        Assert.All(usages, u => Assert.Equal("cop-proj", u.Project));
        Assert.All(usages, u => Assert.Equal("gpt-5", u.Model));

        var shutdown = usages.Where(u => !u.Key.EndsWith(":activity")).ToList();
        Assert.Equal(2, shutdown.Count);
        // First shutdown: whole totals (reasoning is included in output by the reader's contract: tokens keep it separately).
        Assert.Equal(new AgentTokens(3000, 0, 1000, 400, 50), shutdown[0].Billable.Tokens);
        // Second shutdown (resumed session): only the growth.
        Assert.Equal(new AgentTokens(500, 0, 200, 50, 10), shutdown[1].Billable.Tokens);
        Assert.NotEqual(shutdown[0].Key, shutdown[1].Key);
    }

    [Fact]
    public void OnlyAFinalResponseTurnEndFinishesTheTurn()
    {
        var state = new LogState();
        var e = T.ParseFixture(Reader, "copilot-events.jsonl", state);
        var turnEntries = e.Where(x => x is LogEntry.TurnBegan or LogEntry.TurnActive or LogEntry.TurnEnded).ToList();
        Assert.IsType<LogEntry.TurnBegan>(turnEntries[0]);   // user.message
        Assert.IsType<LogEntry.TurnActive>(turnEntries[1]);  // assistant.turn_start while open
        var ended = Assert.Single(turnEntries.OfType<LogEntry.TurnEnded>());
        Assert.True(ended.Completed);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 10, 0, 6, TimeSpan.Zero), ended.At);   // the second turn_end, after the final response
    }

    [Fact]
    public void ShutdownGrowthNeverGoesNegativeWhenCountersReset()
    {
        var state = new LogState { Session = "s" };
        string Shutdown(string id, int input) =>
            $$"""{"type":"session.shutdown","id":"{{id}}","timestamp":"2026-11-15T10:05:00Z","data":{"modelMetrics":{"m":{"usage":{"inputTokens":{{input}},"outputTokens":10} } } } }""";
        var e = T.Parse(Reader, state, [Shutdown("a", 1000), Shutdown("b", 200)]);
        var tokens = e.OfType<LogEntry.Usage>().Select(u => u.Billable.Tokens).ToList();
        Assert.Equal(1000, tokens[0].Input);
        Assert.Equal(200, tokens[1].Input);   // a smaller total means a new counter: count it whole, never negative
    }

    [Fact]
    public void AbortAndShutdownCloseAnOpenTurnWithoutNotice()
    {
        var state = new LogState();
        var e = T.Parse(Reader, state, [
            """{"type":"user.message","timestamp":"2026-11-15T10:00:00Z","data":{}}""",
            """{"type":"abort","timestamp":"2026-11-15T10:00:10Z","data":{}}"""]);
        Assert.False(Assert.IsType<LogEntry.TurnEnded>(e.Last()).Completed);
    }

    [Fact]
    public void SubagentLifecycleNeverChangesTheRootTurn()
    {
        var state = new LogState();
        var e = T.Parse(Reader, state, [
            """{"type":"user.message","timestamp":"2026-11-15T10:00:00Z","data":{}}""",
            """{"type":"abort","agentId":"sub1","timestamp":"2026-11-15T10:00:10Z","data":{}}"""]);
        Assert.True(state.TurnOpen);
        Assert.Single(e);
    }

    [Fact]
    public void UnrelatedEventsAndGarbageAreIgnored()
    {
        var state = new LogState();
        Assert.Empty(T.Parse(Reader, state, ["""{"type":"tool.execution_start","data":{}}""", "nope", """{"type":"assistant.message"}"""]));
    }
}
