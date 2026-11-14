// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using ZstdSharp;
using Lintel.Agents.Accounts;
using Lintel.Agents.Logs;
using Lintel.Agents.Pricing;
using Lintel.Agents.Usage;
using Lintel.Core.Agents;

namespace Lintel.Agents.Tests;

public sealed class LogIndexerTests : IDisposable
{
    private readonly string root = T.TempDir();
    private readonly FakeTime time = new(T.Now);
    private readonly List<AgentUsageEvent> events = [];
    private readonly Func<AgentProvider, bool> enabled;
    private UsageStore store = new(new AgentPricing(PriceSource.Embedded()));
    private TurnTracker turns = new();
    private LogIndexer indexer;

    private string ClaudeDir => Path.Combine(root, "claude");
    private string CodexHome => Path.Combine(root, "codex");
    private string CodexWork => Path.Combine(root, "codex-work");
    private string CopilotDir => Path.Combine(root, "copilot", "session-state");
    private string CachePath => Path.Combine(root, "cache", "usage-cache.json");

    public LogIndexerTests() : this(_ => true) { }

    private LogIndexerTests(Func<AgentProvider, bool> enabled)
    {
        this.enabled = enabled;
        indexer = NewIndexer();
    }

    public void Dispose() => Directory.Delete(root, true);

    private LogRoots Roots() => new()
    {
        ClaudeConfigDir = ClaudeDir,
        CodexHomes = [(CodexAccounts.DefaultId, CodexHome), ("codex:work", CodexWork)],
        CopilotSessionState = CopilotDir,
        OpenCodeDb = Path.Combine(root, "opencode", "opencode.db"),
    };

    private LogIndexer NewIndexer(string? cache = null, Func<AgentProvider, bool>? on = null)
    {
        var i = new LogIndexer(Roots, store, turns, time, on ?? enabled, cache, null) { OnEvent = events.Add };
        return i;
    }

    private string Put(string path, string content, DateTimeOffset? mtime = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, (mtime ?? time.GetUtcNow().AddMinutes(-5)).UtcDateTime);
        return path;
    }

    private void Append(string path, string content, DateTimeOffset? mtime = null)
    {
        File.AppendAllText(path, content);
        File.SetLastWriteTimeUtc(path, (mtime ?? time.GetUtcNow()).UtcDateTime);
    }

    private AgentTotals Month() => UsageSummary.Build(store.Buckets, time.GetUtcNow(), TimeZoneInfo.Utc).Periods[AgentPeriod.Month].Total;
    private static void AssertSame(AgentTotals a, AgentTotals b)
    {
        Assert.Equal(a.Tokens, b.Tokens);
        Assert.Equal(a.Requests, b.Requests);
        Assert.Equal(a.Cost, b.Cost, 12);
    }

    private UsageView View() => UsageSummary.Build(store.Buckets, time.GetUtcNow(), TimeZoneInfo.Utc);
    private string ClaudeFile(string name = "sess.jsonl") => Path.Combine(ClaudeDir, "projects", "demo", name);

    private static string At(DateTimeOffset t) => t.ToString("O");

    private static string CUser(DateTimeOffset t, string session = "sess") =>
        $$"""{"type":"user","sessionId":"{{session}}","cwd":"C:\\work\\live-proj","timestamp":"{{At(t)}}","message":{"role":"user","content":"hi"} }""" + "\n";

    private static string CAssistant(DateTimeOffset t, string stop, string id, int output = 10, string session = "sess", bool sidechain = false, string model = "claude-opus-5-5") =>
        $$"""{"type":"assistant","isSidechain":{{(sidechain ? "true" : "false")}},"sessionId":"{{session}}","cwd":"C:\\work\\live-proj","requestId":"r-{{id}}","timestamp":"{{At(t)}}","message":{"id":"{{id}}","model":"{{model}}","stop_reason":{{stop}},"usage":{"input_tokens":5,"output_tokens":{{output}}},"content":[] } }""" + "\n";

    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void IndexesAllFourLogLocationsIntoOneSummary()
    {
        Put(ClaudeFile(), T.Fixture("claude-session.jsonl"));
        Put(Path.Combine(CodexHome, "sessions", "2026", "11", "15", "rollout-a.jsonl"), T.Fixture("codex-session.jsonl"));
        Put(Path.Combine(CodexHome, "archived_sessions", "rollout-b.jsonl"), T.Fixture("codex-totals.jsonl"));
        Put(Path.Combine(CopilotDir, "cp1", "events.jsonl"), T.Fixture("copilot-events.jsonl"));

        Assert.True(indexer.Pass(discover: true));
        Assert.Equal(4, indexer.TrackedFiles);

        var view = View();
        var month = view.Periods[AgentPeriod.Month];
        Assert.Equal([AgentProvider.Claude, AgentProvider.Codex, AgentProvider.Copilot], view.Seen);
        // Claude: three distinct responses (msg1 final counts, a sidechain reply, msg2).
        var claude = month.ByProvider[AgentProvider.Claude];
        Assert.Equal(3, claude.Requests);
        Assert.Equal(new AgentTokens(35, 1000, 5100, 350, 0), claude.Tokens);
        // Codex: r1 once (duplicate dropped) + the running-totals log (two deltas).
        var codex = month.ByProvider[AgentProvider.Codex];
        Assert.Equal(3, codex.Requests);
        Assert.Equal(new AgentTokens(400 + 400 + 200, 0, 600 + 100 + 200, 50 + 40 + 60, 20), codex.Tokens);
        // Copilot: two API calls and the shutdown growth.
        var copilot = month.ByProvider[AgentProvider.Copilot];
        Assert.Equal(2, copilot.Requests);
        Assert.Equal(new AgentTokens(3500, 0, 1200, 450, 60), copilot.Tokens);
        Assert.Contains(month.Projects, p => p.Name == "demo-app");
        Assert.Contains(month.Projects, p => p.Name == "codex-proj");
        Assert.Contains(month.Projects, p => p.Name == "cop-proj");
        Assert.Contains(month.Models, p => p.Name == "Opus 5.5" && p.Provider == AgentProvider.Claude);
        Assert.Equal(0, month.Total.Unpriced);   // every model in the fixtures is on the price list
    }

    [Fact]
    public void ClaudeCostMatchesTheFormulaForTheFinalStreamedValues()
    {
        Put(ClaudeFile(), T.Fixture("claude-session.jsonl"));
        indexer.Pass(true);
        var p = new AgentPricing(PriceSource.Embedded());
        var msg1 = p.Cost(new Billable(new AgentTokens(10, 1000, 2000, 120, 0), 400), "claude-opus-5-5").Cost!.Value;
        var sub = p.Cost(new Billable(new AgentTokens(5, 0, 100, 30, 0)), "claude-haiku-5-5").Cost!.Value;
        var msg2 = p.Cost(new Billable(new AgentTokens(20, 0, 3000, 200, 0), 0, Fast: true, Domestic: true, WebSearches: 2), "claude-opus-5-5").Cost!.Value;
        Assert.Equal(msg1 + sub + msg2, Month().Cost, 12);
        Assert.True(Month().Savings > 0);
    }

    [Fact]
    public void ProvidersThatAreSwitchedOffAreNotRead()
    {
        Put(ClaudeFile(), T.Fixture("claude-session.jsonl"));
        Put(Path.Combine(CodexHome, "sessions", "a.jsonl"), T.Fixture("codex-session.jsonl"));
        var only = NewIndexer(on: p => p == AgentProvider.Codex);
        only.Pass(true);
        Assert.Equal(1, only.TrackedFiles);
        Assert.Equal([AgentProvider.Codex], View().Seen);
    }

    [Fact]
    public void TailingPicksUpAppendedLinesAndWaitsForTheEndOfAPartialOne()
    {
        var file = Put(ClaudeFile(), CUser(time.GetUtcNow().AddMinutes(-4)) + CAssistant(time.GetUtcNow().AddMinutes(-4), "\"end_turn\"", "m1"));
        indexer.Pass(true);
        Assert.Equal(1, Month().Requests);

        var line = CAssistant(time.GetUtcNow().AddMinutes(-3), "\"end_turn\"", "m2", output: 99);
        Append(file, line[..40]);                       // writer is mid-line
        Assert.False(indexer.Pass(false));
        Assert.Equal(1, Month().Requests);
        Append(file, line[40..]);
        Assert.True(indexer.Pass(false));
        Assert.Equal(2, Month().Requests);
        Assert.Equal(10 + 99, Month().Tokens.Output);
    }

    [Fact]
    public void QuietFilesAreOnlyCheckedOnDiscoveryTicks()
    {
        var file = Put(ClaudeFile(), CAssistant(time.GetUtcNow().AddHours(-3), "\"end_turn\"", "m1"), mtime: time.GetUtcNow().AddHours(-3));
        indexer.Pass(true);
        Append(file, CAssistant(time.GetUtcNow().AddMinutes(-1), "\"end_turn\"", "m2"), mtime: time.GetUtcNow().AddHours(-3));   // sneaks in without touching the mtime
        indexer.Pass(false);
        Assert.Equal(1, Month().Requests);      // not hot, not discovery: left alone
        indexer.Pass(true);
        Assert.Equal(2, Month().Requests);
    }

    [Fact]
    public void AReplacedLogDoesNotDoubleCountAndItsOpenTurnIsForgotten()
    {
        var now = time.GetUtcNow();
        var file = Put(ClaudeFile(), CUser(now.AddMinutes(-4)) + CAssistant(now.AddMinutes(-4), "null", "m1", output: 30));
        indexer.Pass(true);
        Assert.Single(turns.Live(now));

        // Replaced by a shorter file that begins with the same response: re-read from the start, nothing counted twice.
        Put(ClaudeFile(), CAssistant(now.AddMinutes(-4), "\"end_turn\"", "m1", output: 30), mtime: now);
        indexer.Pass(true);
        Assert.Equal(1, Month().Requests);
        Assert.Equal(30, Month().Tokens.Output);
        Assert.Empty(turns.Live(now));

        Append(file, CAssistant(now.AddMinutes(-1), "\"end_turn\"", "m2", output: 7));
        indexer.Pass(true);
        Assert.Equal(2, Month().Requests);
        Assert.Equal(30 + 7, Month().Tokens.Output);
    }

    [Fact]
    public void RestartsResumeFromTheCacheWithoutRereadingOrDoubleCounting()
    {
        var now = time.GetUtcNow();
        var file = Put(ClaudeFile(), CUser(now.AddMinutes(-9)) + CAssistant(now.AddMinutes(-9), "\"end_turn\"", "m1", output: 20));
        Put(Path.Combine(CodexHome, "sessions", "a.jsonl"), T.Fixture("codex-session.jsonl"));
        var first = NewIndexer(CachePath);
        first.Pass(true);
        first.Save();
        var before = Month();
        Assert.True(File.Exists(CachePath));

        // A new process: empty store and tracker, same cache.
        store = new UsageStore(new AgentPricing(PriceSource.Embedded()));
        turns = new TurnTracker();
        var second = NewIndexer(CachePath);
        AssertSame(before, Month());                    // aggregates come back from the cache
        Assert.False(second.Pass(true));                // nothing new to read
        AssertSame(before, Month());

        Append(file, CAssistant(now.AddMinutes(-1), "\"end_turn\"", "m2", output: 5));
        Assert.True(second.Pass(false));
        Assert.Equal(before.Requests + 1, Month().Requests);
        Assert.Equal(before.Tokens.Output + 5, Month().Tokens.Output);
    }

    // ---- Codex .jsonl.zst -------------------------------------------------------------------------------------------

    private static byte[] Zst(string text)
    {
        using var c = new Compressor();
        return c.Wrap(Encoding.UTF8.GetBytes(text)).ToArray();
    }

    private static string PutZst(string path, byte[] bytes, DateTimeOffset mtime)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, mtime.UtcDateTime);
        return path;
    }

    private string PutZst(string path, byte[] bytes) => PutZst(path, bytes, time.GetUtcNow().AddMinutes(-5));

    private string CodexZstPath => Path.Combine(CodexHome, "sessions", "rollout-z.jsonl.zst");

    [Theory]
    [InlineData("codex-session.jsonl")]
    [InlineData("codex-totals.jsonl")]
    public void CompressedCodexLogsGiveTheSameResultAsPlainOnes(string fixture)
    {
        var plain = Put(Path.Combine(CodexHome, "sessions", "rollout-p.jsonl"), T.Fixture(fixture));
        indexer.Pass(true);
        var expected = View().Periods[AgentPeriod.Month];
        Assert.True(expected.Total.Requests > 0);
        File.Delete(plain);

        store = new UsageStore(new AgentPricing(PriceSource.Embedded()));
        turns = new TurnTracker();
        PutZst(CodexZstPath, Zst(T.Fixture(fixture)));
        var zst = NewIndexer();
        Assert.True(zst.Pass(true));
        Assert.Equal(1, zst.TrackedFiles);
        var actual = View().Periods[AgentPeriod.Month];
        AssertSame(expected.Total, actual.Total);
        Assert.Equal(expected.Models.Select(m => (m.Name, m.Totals.Tokens)), actual.Models.Select(m => (m.Name, m.Totals.Tokens)));
        Assert.Equal(expected.Projects.Select(m => m.Name), actual.Projects.Select(m => m.Name));
        Assert.False(zst.Pass(true));   // unchanged on disk: not decompressed again
    }

    private static (string Head, string All) TotalsSplit()
    {
        var lines = T.Fixture("codex-totals.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 3);
        return (string.Concat(lines.Take(lines.Length - 1).Select(l => l + "\n")), string.Concat(lines.Select(l => l + "\n")));
    }

    [Fact]
    public void AGrowingCompressedLogOnlyCountsWhatIsNew()
    {
        var (head, all) = TotalsSplit();
        PutZst(CodexZstPath, Zst(head));
        indexer.Pass(true);
        var partial = Month();

        PutZst(CodexZstPath, Zst(all), time.GetUtcNow());
        Assert.True(indexer.Pass(true));
        var full = Month();
        Assert.True(full.Requests > partial.Requests);

        // Same content read plain from scratch gives the same totals.
        store = new UsageStore(new AgentPricing(PriceSource.Embedded()));
        turns = new TurnTracker();
        File.Delete(CodexZstPath);
        Put(Path.Combine(CodexHome, "sessions", "rollout-p.jsonl"), all);
        NewIndexer().Pass(true);
        AssertSame(Month(), full);
    }

    [Fact]
    public void AShorterReplacementArchiveIsReadFromTheStartWithoutDoubleCounting()
    {
        var (head, all) = TotalsSplit();
        PutZst(CodexZstPath, Zst(all));
        indexer.Pass(true);
        var full = Month();
        PutZst(CodexZstPath, Zst(head), time.GetUtcNow());
        indexer.Pass(true);
        AssertSame(full, Month());
    }

    [Fact]
    public void ATruncatedOrCorruptCompressedLogIsSkippedWithWarnings()
    {
        var good = Zst(T.Fixture("codex-session.jsonl"));
        PutZst(Path.Combine(CodexHome, "sessions", "cut.jsonl.zst"), good[..(good.Length / 2)]);
        PutZst(Path.Combine(CodexHome, "sessions", "junk.jsonl.zst"), Encoding.UTF8.GetBytes("this is not zstd data at all"));
        PutZst(Path.Combine(CodexHome, "sessions", "empty.jsonl.zst"), []);
        Put(Path.Combine(CodexHome, "archived_sessions", "ok.jsonl"), T.Fixture("codex-session.jsonl"));

        var warnings = new List<string>();
        var i = new LogIndexer(Roots, store, turns, time, enabled, null, warnings.Add);
        i.Pass(true);
        Assert.NotEmpty(warnings);
        Assert.DoesNotContain(warnings, w => w.Contains(root));   // file names only, never folders
        var count = warnings.Count;
        i.Pass(true);
        Assert.Equal(count, warnings.Count);                       // not retried until the file changes
        Assert.True(Month().Requests > 0);                         // the healthy log still counted
    }

    // ---- no double counting after a restart -------------------------------------------------------------------------

    [Fact]
    public void ALogRewrittenFromTheStartAfterARestartIsNotCountedTwice()
    {
        var now = time.GetUtcNow();
        var file = Put(ClaudeFile(), CUser(now.AddMinutes(-9)) + CAssistant(now.AddMinutes(-9), "\"end_turn\"", "m1", output: 20));
        var first = NewIndexer(CachePath);
        first.Pass(true);
        first.Save();
        var before = Month();
        Assert.Equal(1, before.Requests);

        store = new UsageStore(new AgentPricing(PriceSource.Embedded()));
        turns = new TurnTracker();
        var second = NewIndexer(CachePath);
        // Shorter than the saved offset, so it is read from byte 0 again, starting with the same response.
        Put(ClaudeFile(), CAssistant(now.AddMinutes(-9), "\"end_turn\"", "m1", output: 20), now);
        second.Pass(true);
        AssertSame(before, Month());

        Append(file, CAssistant(now.AddMinutes(-1), "\"end_turn\"", "m2", output: 5));
        second.Pass(false);
        Assert.Equal(2, Month().Requests);
    }

    [Fact]
    public void ACompressedLogChangedAfterARestartIsNotCountedTwice()
    {
        var (head, all) = TotalsSplit();
        PutZst(CodexZstPath, Zst(head));
        var first = NewIndexer(CachePath);
        first.Pass(true);
        first.Save();
        var partial = Month();

        store = new UsageStore(new AgentPricing(PriceSource.Embedded()));
        turns = new TurnTracker();
        var second = NewIndexer(CachePath);
        AssertSame(partial, Month());
        Assert.False(second.Pass(true));                           // same size and time: nothing is decompressed
        PutZst(CodexZstPath, Zst(all), time.GetUtcNow());
        Assert.True(second.Pass(true));
        Assert.True(Month().Requests > partial.Requests);
    }

    [Fact]
    public void FinishedNoticeFollowsTheRules()
    {
        var now = time.GetUtcNow();
        turns.ReportsTransitions = true;
        Put(ClaudeFile("a.jsonl"), CUser(now.AddSeconds(-134), "a") + CAssistant(now.AddSeconds(-14), "\"end_turn\"", "ma", output: 100, session: "a"));
        // Interrupted turn: no notice.
        Put(ClaudeFile("b.jsonl"), CUser(now.AddMinutes(-3), "b") + CAssistant(now.AddMinutes(-2), "null", "mb", session: "b") +
            $$"""{"type":"user","sessionId":"b","cwd":"C:\\work\\live-proj","timestamp":"{{At(now.AddMinutes(-1))}}","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]} }""" + "\n");
        // Ended long ago (history found late): no notice.
        Put(ClaudeFile("c.jsonl"), CUser(now.AddHours(-2), "c") + CAssistant(now.AddHours(-1), "\"end_turn\"", "mc", session: "c"));

        indexer.Pass(true);
        var finished = Assert.IsType<AgentUsageEvent.Finished>(Assert.Single(events));
        Assert.Equal(AgentProvider.Claude, finished.Provider);
        Assert.Equal(TimeSpan.FromSeconds(120), finished.Duration);
        Assert.Equal("live-proj", finished.Project);
        Assert.Equal(105, finished.Tokens);
        Assert.NotNull(finished.Cost);
        Assert.True(finished.Cost > 0);

        indexer.Pass(true);
        Assert.Single(events);   // never twice
    }

    [Fact]
    public void FirstPassHistoryNeverRaisesFinished()
    {
        var now = time.GetUtcNow();
        Put(ClaudeFile(), CUser(now.AddMinutes(-3)) + CAssistant(now.AddMinutes(-1), "\"end_turn\"", "m1"));
        indexer.Pass(true);   // ReportsTransitions is still false
        Assert.Empty(events);
    }

    [Fact]
    public void LiveSessionsShowWhileActiveAndFadeAfterTenSilentMinutes()
    {
        var now = time.GetUtcNow();
        Put(ClaudeFile(), CUser(now.AddMinutes(-2)) + CAssistant(now.AddMinutes(-1), "null", "m1", output: 250), mtime: now.AddMinutes(-1));
        indexer.Pass(true);

        var live = Assert.Single(turns.Live(now));
        Assert.Equal(AgentProvider.Claude, live.Provider);
        Assert.Equal("live-proj", live.Project);
        Assert.Equal("claude-opus-5-5", live.Model);
        Assert.Equal(255, live.Tokens.Total);
        Assert.True(live.Cost > 0);
        Assert.Equal(now.AddMinutes(-2), live.Started);

        Assert.Empty(turns.Live(now.AddMinutes(10)));
    }

    [Fact]
    public void ClaudeSubagentLogsCountTowardTheParentTurn()
    {
        var now = time.GetUtcNow();
        Put(ClaudeFile("sess1.jsonl"), CUser(now.AddMinutes(-3), "sess1") + CAssistant(now.AddMinutes(-3), "null", "p1", output: 10, session: "sess1"),
            mtime: now.AddMinutes(-3));
        var sub = Path.Combine(ClaudeDir, "projects", "demo", "sess1", "subagents", "agent-a1.jsonl");
        Put(sub, CAssistant(now.AddMinutes(-2), "\"end_turn\"", "s1", output: 40, session: "sess1", sidechain: true, model: "claude-haiku-5-5"), mtime: now.AddMinutes(-2));

        indexer.Pass(true);
        var live = Assert.Single(turns.Live(now));
        Assert.Equal(5 + 10 + 5 + 40, live.Tokens.Total);        // subagent tokens joined the parent's turn
        Assert.Equal("claude-opus-5-5", live.Model);            // ...without changing its model
        Assert.Equal(2, Month().Requests);
    }

    [Fact]
    public void CodexSubagentUsageJoinsTheRootThreadsTurn()
    {
        var now = time.GetUtcNow();
        Put(Path.Combine(CodexHome, "sessions", "root.jsonl"),
            $$"""{"timestamp":"{{At(now.AddMinutes(-3))}}","type":"session_meta","payload":{"id":"sess-root","cwd":"C:\\work\\codex-proj","source":"cli"} }""" + "\n" +
            $$"""{"timestamp":"{{At(now.AddMinutes(-3))}}","type":"turn_context","payload":{"model":"gpt-6.1-sol"} }""" + "\n" +
            $$"""{"timestamp":"{{At(now.AddMinutes(-3))}}","type":"event_msg","payload":{"type":"task_started"} }""" + "\n", now.AddMinutes(-3));
        Put(Path.Combine(CodexHome, "sessions", "sub.jsonl"), T.Fixture("codex-subagent.jsonl").Replace("2026-11-15T10:00:10.000Z", At(now.AddMinutes(-2))), now.AddMinutes(-2));

        indexer.Pass(true);
        var live = Assert.Single(turns.Live(now));    // the subagent file opens no turn of its own
        Assert.Equal(AgentProvider.Codex, live.Provider);
        Assert.Equal(15, live.Tokens.Total);
        Assert.Equal("codex-proj", live.Project);
    }

    [Fact]
    public void CodexLimitsAreKeptPerAccountAndTheNewestReadingWins()
    {
        var now = time.GetUtcNow();
        string Limits(DateTimeOffset t, double used, string plan) =>
            $$"""{"timestamp":"{{At(t)}}","type":"event_msg","payload":{"type":"token_count","rate_limits":{"limit_id":"codex","plan_type":"{{plan}}","primary":{"used_percent":{{used}},"window_minutes":300,"resets_at":1794232800} } } }""" + "\n";
        Put(Path.Combine(CodexHome, "sessions", "new.jsonl"), Limits(now.AddMinutes(-10), 30, "plus"));
        Put(Path.Combine(CodexHome, "archived_sessions", "old.jsonl"), Limits(now.AddDays(-3), 90, "free"), now.AddMinutes(-9));   // read later but older
        Put(Path.Combine(CodexWork, "sessions", "w.jsonl"), Limits(now.AddMinutes(-20), 5, "pro"));

        indexer.Pass(true);
        Assert.Equal(30, indexer.CodexLimits[CodexAccounts.DefaultId].Windows[0].UsedPercent);
        Assert.Equal(now.AddMinutes(-10), indexer.CodexLimits[CodexAccounts.DefaultId].ObservedAt);
        Assert.Equal("plus", indexer.CodexPlans[CodexAccounts.DefaultId].Name);
        Assert.Equal(5, indexer.CodexLimits["codex:work"].Windows[0].UsedPercent);
        Assert.Equal("pro", indexer.CodexPlans["codex:work"].Name);
    }

    [Fact]
    public void FilesOlderThanThirtyDaysAreIgnoredAndDeletedFilesAreForgotten()
    {
        var now = time.GetUtcNow();
        Put(ClaudeFile("ancient.jsonl"), CAssistant(now.AddDays(-40), "\"end_turn\"", "x"), now.AddDays(-40));
        var live = Put(ClaudeFile("live.jsonl"), CUser(now.AddMinutes(-1)) + CAssistant(now.AddMinutes(-1), "null", "y"), now.AddMinutes(-1));
        indexer.Pass(true);
        Assert.Equal(1, indexer.TrackedFiles);
        Assert.Single(turns.Live(now));

        File.Delete(live);
        indexer.Pass(true);
        Assert.Equal(0, indexer.TrackedFiles);
        Assert.Empty(turns.Live(now));
    }

    [Fact]
    public void MissingRootsAndDamagedLinesAreHarmless()
    {
        Assert.False(indexer.Pass(true));   // no folder exists at all
        Put(ClaudeFile(), "garbage\n{\"type\":\"assistant\"\n\n" + CAssistant(time.GetUtcNow().AddMinutes(-1), "\"end_turn\"", "ok") + "\u0000\u0001\n");
        indexer.Pass(true);
        Assert.Equal(1, Month().Requests);
    }

    [Fact]
    public void OpenCodeRowsFlowThroughWhenTheDatabaseExists()
    {
        var path = Path.Combine(root, "opencode", "opencode.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var db = WinSqlite.OpenReadWrite(path)!)
        {
            db.Exec("CREATE TABLE session(id TEXT PRIMARY KEY, directory TEXT)");
            db.Exec("CREATE TABLE message(id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, time_updated INTEGER, data TEXT)");
            db.Exec("INSERT INTO session VALUES ('s1','C:\\p\\oc')");
            var ms = time.GetUtcNow().AddMinutes(-30).ToUnixTimeMilliseconds();
            Assert.True(db.Exec($$"""INSERT INTO message VALUES ('m1','s1',{{ms}},{{ms}},'{"role":"assistant","modelID":"claude-opus-5-5","tokens":{"input":1000,"output":100,"reasoning":0,"cache":{"read":0,"write":0} },"cost":0.2,"time":{"created":1,"completed":2} }')"""));
        }
        indexer.Pass(true);
        Assert.Contains(AgentProvider.OpenCode, indexer.Present);
        var openCode = View().Periods[AgentPeriod.Month].ByProvider[AgentProvider.OpenCode];
        Assert.Equal(1, openCode.Requests);
        Assert.Equal(1100, openCode.Tokens.Total);
        Assert.Equal(1000 * 4 / 1e6 + 100 * 20 / 1e6, openCode.Cost, 12);   // list price wins over the recorded 0.2

        indexer.Pass(true);   // unchanged database: nothing is read twice
        Assert.Equal(1, View().Periods[AgentPeriod.Month].ByProvider[AgentProvider.OpenCode].Requests);
    }

    [Fact]
    public void TheCacheHoldsNoPromptsTokensOrEmailAddresses()
    {
        Put(ClaudeFile(), CUser(time.GetUtcNow().AddMinutes(-1)).Replace("\"hi\"", "\"my secret prompt about someone@example.com\"") +
            CAssistant(time.GetUtcNow().AddMinutes(-1), "\"end_turn\"", "m1"));
        var cached = NewIndexer(CachePath);
        cached.Pass(true);
        cached.Save();
        var text = File.ReadAllText(CachePath);
        Assert.DoesNotContain("secret prompt", text);
        Assert.DoesNotContain("example.com", text);
        Assert.DoesNotContain("@", text);
        Assert.DoesNotContain("sk-", text);
    }

    [Fact]
    public void SubagentFileDetectionHandlesBothSeparators()
    {
        Assert.True(LogIndexer.IsClaudeSubagentFile(@"C:\x\projects\p\sess\subagents\agent-1.jsonl"));
        Assert.True(LogIndexer.IsClaudeSubagentFile("/x/projects/p/sess/subagents/agent-1.jsonl"));
        Assert.False(LogIndexer.IsClaudeSubagentFile(@"C:\x\projects\p\sess.jsonl"));
    }

    [Fact]
    public void DiscoveryReportsEveryRootsFilesOldestFirst()
    {
        var now = time.GetUtcNow();
        Put(ClaudeFile("newer.jsonl"), "{}\n", now.AddMinutes(-1));
        Put(ClaudeFile("older.jsonl"), "{}\n", now.AddHours(-1));
        Put(Path.Combine(CodexWork, "archived_sessions", "x.jsonl"), "{}\n", now.AddMinutes(-30));
        Put(Path.Combine(CopilotDir, "cp9", "events.jsonl"), "{}\n", now.AddMinutes(-10));
        Put(Path.Combine(CopilotDir, "cp9", "other.jsonl"), "{}\n", now.AddMinutes(-10));   // only events.jsonl counts
        var found = Roots().Discover(now.UtcDateTime.AddDays(-30), _ => true);
        Assert.Equal(["older.jsonl", "x.jsonl", "events.jsonl", "newer.jsonl"], found.Select(f => Path.GetFileName(f.Path)));
        Assert.Equal("codex:work", found.Single(f => f.Path.EndsWith("x.jsonl")).AccountId);
        Assert.All(found.Where(f => f.Provider != AgentProvider.Codex), f => Assert.Null(f.AccountId));
    }
}
