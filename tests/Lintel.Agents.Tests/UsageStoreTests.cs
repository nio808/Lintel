// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Agents.Logs;
using Lintel.Agents.Pricing;
using Lintel.Agents.Usage;
using Lintel.Core.Agents;

namespace Lintel.Agents.Tests;

public sealed class UsageStoreTests
{
    private static UsageStore NewStore() => new(new AgentPricing(PriceSource.Embedded()));

    private static LogEntry.Usage U(string key, DateTimeOffset date, AgentTokens tokens, string model = "claude-opus-5-5",
                                    string project = "proj", AgentProvider provider = AgentProvider.Claude, int requests = 1, double? reported = null,
                                    Billable? billable = null) =>
        new(key, provider, date, model, project, "s", requests, billable ?? new Billable(tokens), reported);

    [Fact]
    public void AddsIntoHourlyBucketsKeyedByProviderModelProject()
    {
        var store = NewStore();
        var t = new DateTimeOffset(2026, 11, 15, 10, 20, 0, TimeSpan.Zero);
        store.Add(U("a", t, new AgentTokens(1_000_000, 0, 0, 0, 0)), T.Now);
        store.Add(U("b", t.AddMinutes(30), new AgentTokens(0, 0, 0, 1_000_000, 0)), T.Now);                    // same hour
        store.Add(U("c", t.AddHours(1), new AgentTokens(1, 0, 0, 0, 0)), T.Now);                                // next hour
        store.Add(U("d", t, new AgentTokens(1, 0, 0, 0, 0), project: "other"), T.Now);                          // other project

        Assert.Equal(3, store.Buckets.Count);
        var b = store.Buckets[new BucketKey(UsageStore.HourOf(t), AgentProvider.Claude, "claude-opus-5-5", "proj")];
        Assert.Equal(2, b.Requests);
        Assert.Equal(1_000_000, b.Tokens.Input);
        Assert.Equal(4 + 20, b.Cost, 9);
    }

    [Fact]
    public void RepeatedKeysKeepThePerFieldMaximumAndCountTheRequestOnce()
    {
        var store = NewStore();
        var t = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
        var first = store.Add(U("k", t, new AgentTokens(100, 1000, 2000, 5, 0)), T.Now);
        var second = store.Add(U("k", t.AddSeconds(1), new AgentTokens(100, 1000, 2000, 120, 0)), T.Now);   // streamed chunk with more output
        var third = store.Add(U("k", t.AddSeconds(2), new AgentTokens(100, 1000, 2000, 60, 0)), T.Now);     // a smaller repeat is ignored

        Assert.True(first.Changed);
        Assert.True(second.Changed);
        Assert.Equal(115, second.Tokens.Output);
        Assert.False(third.Changed);

        var b = Assert.Single(store.Buckets.Values);
        Assert.Equal(1, b.Requests);
        Assert.Equal(120, b.Tokens.Output);
        var price = new AgentPricing(PriceSource.Embedded());
        var expected = price.Cost(new Billable(new AgentTokens(100, 1000, 2000, 120, 0)), "claude-opus-5-5");
        Assert.Equal(expected.Cost!.Value, b.Cost, 12);
        Assert.Equal(expected.Savings, b.Savings, 12);
        Assert.Equal(b.Cost, first.Cost + second.Cost, 12);   // deltas add up to the bucket (what a live turn accumulates)
    }

    [Fact]
    public void ADuplicateFromAnotherFileIsCountedOnce()
    {
        var store = NewStore();
        var t = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
        store.Add(U("claude:m:r", t, new AgentTokens(10, 0, 0, 10, 0)), T.Now);
        Assert.False(store.Add(U("claude:m:r", t, new AgentTokens(10, 0, 0, 10, 0)), T.Now).Changed);   // a resumed session copies old messages
        Assert.Equal(1, Assert.Single(store.Buckets.Values).Requests);
    }

    [Fact]
    public void UnknownModelsAreUnpricedNeverGuessed()
    {
        var store = NewStore();
        var d = store.Add(U("x", T.Now.AddHours(-1), new AgentTokens(1000, 0, 0, 10, 0), model: "mystery-model"), T.Now);
        var b = Assert.Single(store.Buckets.Values);
        Assert.Equal(0, b.Cost);
        Assert.Equal(1, b.Unpriced);
        Assert.Equal(1, d.Unpriced);
        Assert.Equal(1010, b.Tokens.Total);
    }

    [Fact]
    public void ACostTheProviderRecordedStandsInForAnUnknownModel()
    {
        var store = NewStore();
        store.Add(U("x", T.Now.AddHours(-1), new AgentTokens(1000, 0, 0, 10, 0), model: "mystery-model", provider: AgentProvider.OpenCode, reported: 0.42), T.Now);
        var b = Assert.Single(store.Buckets.Values);
        Assert.Equal(0.42, b.Cost, 12);
        Assert.Equal(0, b.Unpriced);
        // ...but a model the list knows is priced by the list, whatever was recorded.
        var other = NewStore();
        other.Add(U("y", T.Now.AddHours(-1), new AgentTokens(1_000_000, 0, 0, 0, 0), provider: AgentProvider.OpenCode, reported: 99), T.Now);
        Assert.Equal(4, Assert.Single(other.Buckets.Values).Cost, 12);
    }

    [Fact]
    public void ActivityOnlyRecordsCountRequestsWithoutCost()
    {
        var store = NewStore();
        store.Add(U("a", T.Now.AddHours(-1), default, model: "", provider: AgentProvider.Copilot, billable: new Billable(default, IsAggregate: true)), T.Now);
        var b = Assert.Single(store.Buckets.Values);
        Assert.Equal(1, b.Requests);
        Assert.Equal(0, b.Unpriced);
        Assert.Equal(0, b.Cost);
    }

    [Fact]
    public void RetentionIsThirtyDaysForBothAddAndPrune()
    {
        var store = NewStore();
        Assert.False(store.Add(U("old", T.Now.AddDays(-31), new AgentTokens(1, 0, 0, 0, 0)), T.Now).Changed);   // too old: not added
        Assert.Empty(store.Buckets);

        store.Add(U("edge", T.Now.AddDays(-29), new AgentTokens(1, 0, 0, 0, 0)), T.Now);
        store.Add(U("now", T.Now.AddHours(-1), new AgentTokens(1, 0, 0, 0, 0)), T.Now);
        Assert.Equal(2, store.Buckets.Count);

        Assert.False(store.Prune(T.Now));                         // nothing is 30 days old yet
        Assert.True(store.Prune(T.Now.AddDays(2)));               // the 29-days-ago bucket is now beyond 30 days
        var left = Assert.Single(store.Buckets);
        Assert.True(left.Key.Hour >= UsageStore.HourOf(T.Now.AddDays(2) - UsageStore.Retention));
    }

    [Fact]
    public void FutureTimestampsFromDamagedLinesAreRejected()
    {
        var store = NewStore();
        Assert.False(store.Add(U("f", T.Now.AddDays(30), new AgentTokens(1, 0, 0, 0, 0)), T.Now).Changed);
    }

    [Fact]
    public void LoadMergesPersistedBuckets()
    {
        var store = NewStore();
        var key = new BucketKey(100, AgentProvider.Claude, "m", "p");
        store.Load(key, new Bucket { Tokens = new AgentTokens(1, 2, 3, 4, 5), Cost = 1, Requests = 2 });
        store.Load(key, new Bucket { Tokens = new AgentTokens(1, 0, 0, 0, 0), Cost = 0.5, Requests = 1, Unpriced = 1 });
        var b = store.Buckets[key];
        Assert.Equal(new AgentTokens(2, 2, 3, 4, 5), b.Tokens);
        Assert.Equal(1.5, b.Cost);
        Assert.Equal(3, b.Requests);
        Assert.Equal(1, b.Unpriced);
    }

    [Fact]
    public void HourOfFloorsToTheEpochHour() =>
        Assert.Equal(UsageStore.HourOf(new DateTimeOffset(2026, 11, 15, 10, 59, 59, TimeSpan.Zero)), UsageStore.HourOf(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero)));
}

public sealed class UsageSummaryTests
{
    // UTC+2 all year, so "local midnight" is 22:00 UTC of the previous day.
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test", "test");
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);   // 14:00 local, 15 Nov

    private static KeyValuePair<BucketKey, Bucket> B(DateTimeOffset at, long tokens = 100, double cost = 1, AgentProvider provider = AgentProvider.Claude,
                                                     string model = "claude-opus-5-5", string project = "proj", int requests = 1, double savings = 0.5) =>
        new(new BucketKey(UsageStore.HourOf(at), provider, model, project),
            new Bucket { Tokens = new AgentTokens(tokens, 0, 0, 0, 0), Cost = cost, Requests = requests, Savings = savings });

    private static UsageView Build(params KeyValuePair<BucketKey, Bucket>[] buckets) => UsageSummary.Build(buckets, Now, Zone);

    [Fact]
    public void TodayStartsAtLocalMidnight()
    {
        var view = Build(
            B(new DateTimeOffset(2026, 11, 14, 22, 0, 0, TimeSpan.Zero), cost: 1),      // 00:00 local on the 15th: today
            B(new DateTimeOffset(2026, 11, 14, 21, 59, 0, TimeSpan.Zero), cost: 10));   // 23:59 local on the 14th: yesterday
        Assert.Equal(1, view.Periods[AgentPeriod.Today].Total.Cost);
        Assert.Equal(11, view.Periods[AgentPeriod.Week].Total.Cost);
        Assert.Equal(11, view.Periods[AgentPeriod.Month].Total.Cost);
    }

    [Fact]
    public void WeekIsSevenLocalDaysAndMonthIsThirty()
    {
        // Local dates: today = 15 Nov. Week = 9..15 Nov, Month = 17 Oct..15 Nov.
        DateTimeOffset LocalNoon(int month, int day) => new(2026, month, day, 12, 0, 0, TimeSpan.FromHours(2));
        var view = Build(
            B(LocalNoon(11, 9), cost: 1),      // 6 days ago: in the week
            B(LocalNoon(11, 8), cost: 100),    // 7 days ago: out of the week
            B(LocalNoon(10, 17), cost: 1000),  // 29 days ago: in the month
            B(LocalNoon(10, 16), cost: 10000)); // 30 days ago: out
        Assert.Equal(0, view.Periods[AgentPeriod.Today].Total.Cost);
        Assert.Equal(1, view.Periods[AgentPeriod.Week].Total.Cost);
        Assert.Equal(1101, view.Periods[AgentPeriod.Month].Total.Cost);
    }

    [Fact]
    public void DaysAreThirtyLocalMidnightsOldestFirstWithGapsFilledForEveryProvider()
    {
        var view = Build(B(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero), cost: 3, tokens: 50));
        Assert.Equal(30, view.Days.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.FromHours(2)), view.Days[0].Start);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.FromHours(2)), view.Days[29].Start);
        Assert.All(view.Days, d => Assert.Equal(Enum.GetValues<AgentProvider>().Length, d.ByProvider.Count));
        var day = view.Days[27];   // 13 Nov
        Assert.Equal(3, day.ByProvider[AgentProvider.Claude].Cost);
        Assert.Equal(50, day.ByProvider[AgentProvider.Claude].Tokens.Total);
        Assert.Equal(AgentTotals.Zero, day.ByProvider[AgentProvider.Codex]);
        Assert.Equal(AgentTotals.Zero, view.Days[0].ByProvider[AgentProvider.Claude]);
    }

    [Fact]
    public void HoursAreTheLast24WithTheCurrentHourLast()
    {
        var view = Build(
            B(Now.AddHours(-1), cost: 1),
            B(Now, cost: 2),                    // current hour
            B(Now.AddHours(-23), cost: 4),      // oldest hour still inside
            B(Now.AddHours(-24), cost: 8));     // just outside
        Assert.Equal(24, view.Hours.Count);
        Assert.Equal(2, view.Hours[23].ByProvider[AgentProvider.Claude].Cost);
        Assert.Equal(1, view.Hours[22].ByProvider[AgentProvider.Claude].Cost);
        Assert.Equal(4, view.Hours[0].ByProvider[AgentProvider.Claude].Cost);
        Assert.Equal(7, view.Hours.Sum(h => h.ByProvider[AgentProvider.Claude].Cost));
        Assert.True(view.Hours[0].Start < view.Hours[1].Start);
    }

    [Fact]
    public void SharesAreSortedByCostAndNamedReadably()
    {
        var view = Build(
            B(Now.AddHours(-1), cost: 1, model: "claude-haiku-5-5", project: "small"),
            B(Now.AddHours(-2), cost: 5, model: "claude-opus-5-5", project: "big"),
            B(Now.AddHours(-3), cost: 3, model: "claude-sonnet-5-5-20260101", project: "big", provider: AgentProvider.Claude),
            B(Now.AddHours(-4), cost: 2, model: "gpt-6.1-sol", project: "", provider: AgentProvider.Codex));
        var p = view.Periods[AgentPeriod.Today];
        Assert.Equal(["Opus 5.5", "Sonnet 5.5", "GPT-6.1 Sol", "Haiku 5.5"], p.Models.Select(m => m.Name));
        Assert.Equal([AgentProvider.Claude, AgentProvider.Claude, AgentProvider.Codex, AgentProvider.Claude], p.Models.Select(m => m.Provider));
        Assert.Equal(["big", "Unknown", "small"], p.Projects.Select(m => m.Name));
        Assert.Equal(8, p.Projects[0].Totals.Cost);
        Assert.All(p.Projects, s => Assert.Null(s.Provider));
        Assert.Equal(11, p.Total.Cost);
    }

    [Fact]
    public void MergedModelVariantsShareOneRow()
    {
        var view = Build(
            B(Now.AddHours(-1), cost: 1, model: "claude-opus-5-5"),
            B(Now.AddHours(-2), cost: 2, model: "claude-opus-5-5-20260301"));
        var share = Assert.Single(view.Periods[AgentPeriod.Today].Models);
        Assert.Equal(3, share.Totals.Cost);
    }

    [Fact]
    public void ByProviderListsProvidersWithUsageAndSeenFollowsData()
    {
        var view = Build(B(Now.AddHours(-1), provider: AgentProvider.Codex, model: "gpt-6.1-sol"));
        Assert.Equal([AgentProvider.Codex], view.Seen);
        Assert.Equal([AgentProvider.Codex], view.Periods[AgentPeriod.Month].ByProvider.Keys);
        Assert.Empty(Build().Seen);
    }

    [Fact]
    public void DisabledProvidersAreLeftOut()
    {
        var view = UsageSummary.Build([B(Now.AddHours(-1)), B(Now.AddHours(-1), provider: AgentProvider.Codex, model: "gpt-6.1-sol")], Now, Zone, p => p != AgentProvider.Codex);
        Assert.Equal([AgentProvider.Claude], view.Seen);
        Assert.Equal(1, view.Periods[AgentPeriod.Today].Total.Cost);
    }

    [Fact]
    public void EmptyStoreGivesAllThreePeriodsAndFilledBuckets()
    {
        var view = Build();
        Assert.Equal(3, view.Periods.Count);
        Assert.All(view.Periods.Values, p => Assert.Equal(AgentTotals.Zero, p.Total));
        Assert.Equal(30, view.Days.Count);
        Assert.Equal(24, view.Hours.Count);
    }

    [Fact]
    public void SharesAreCappedAtTwenty()
    {
        var buckets = Enumerable.Range(0, 30).Select(i => B(Now.AddHours(-1), cost: i, project: $"p{i:00}")).ToArray();
        Assert.Equal(UsageSummary.TopShares, Build(buckets).Periods[AgentPeriod.Today].Projects.Count);
    }
}

public sealed class UsageCacheTests
{
    [Fact]
    public void RoundTripKeepsBucketsOffsetsAndParserState()
    {
        var dir = T.TempDir();
        var path = Path.Combine(dir, "sub", "usage-cache.json");
        var buckets = new Dictionary<BucketKey, Bucket>
        {
            [new BucketKey(500_000, AgentProvider.Claude, "claude-opus-5-5", "my \"proj\"")] =
                new() { Tokens = new AgentTokens(1, 2, 3, 4, 5), Cost = 1.25, Savings = 0.5, Requests = 7, Unpriced = 1 },
            [new BucketKey(500_001, AgentProvider.Codex, "gpt-6.1-sol", "")] = new() { Tokens = new AgentTokens(9, 0, 0, 0, 0) },
        };
        var state = new LogState
        {
            Session = "s1", Project = "p", Model = "m", TurnOpen = true, Fast = true, LastTotal = new AgentTokens(1, 2, 3, 4, 5),
            CopilotTotals = { ["gpt-5"] = new AgentTokens(7, 0, 0, 8, 0) }, CodexRoot = "root",
        };
        var files = new Dictionary<string, FileProgress>
        {
            [@"C:\x\a.jsonl"] = new() { Provider = AgentProvider.Claude, Offset = 12345, Mtime = 99, State = state },
        };
        UsageCache.Save(path, files, buckets);

        var (loadedFiles, loadedBuckets) = UsageCache.Load(path);
        var f = Assert.Single(loadedFiles);
        Assert.Equal(12345, f.Value.Offset);
        Assert.Equal(99, f.Value.Mtime);
        Assert.Equal(AgentProvider.Claude, f.Value.Provider);
        Assert.Equal("s1", f.Value.State.Session);
        Assert.True(f.Value.State.TurnOpen);
        Assert.True(f.Value.State.Fast);
        Assert.Equal(new AgentTokens(1, 2, 3, 4, 5), f.Value.State.LastTotal);
        Assert.Equal(new AgentTokens(7, 0, 0, 8, 0), f.Value.State.CopilotTotals["gpt-5"]);
        Assert.Equal("root", f.Value.State.CodexRoot);

        Assert.Equal(2, loadedBuckets.Count);
        var first = loadedBuckets.Single(b => b.Key.Provider == AgentProvider.Claude);
        Assert.Equal("my \"proj\"", first.Key.Project);
        Assert.Equal(new AgentTokens(1, 2, 3, 4, 5), first.Bucket.Tokens);
        Assert.Equal((1.25, 0.5, 7, 1), (first.Bucket.Cost, first.Bucket.Savings, first.Bucket.Requests, first.Bucket.Unpriced));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void CacheShapeMatchesThePlanAndHoldsNoLogContent()
    {
        var dir = T.TempDir();
        var path = Path.Combine(dir, "c.json");
        UsageCache.Save(path, new Dictionary<string, FileProgress> { ["f"] = new() { Provider = AgentProvider.Claude } },
            new Dictionary<BucketKey, Bucket> { [new BucketKey(1, AgentProvider.Claude, "m", "j")] = new() });
        var text = File.ReadAllText(path);
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        Assert.Equal(1, doc.RootElement.GetProperty("schema").GetInt32());
        var bucket = doc.RootElement.GetProperty("buckets")[0];
        Assert.Equal(["h", "p", "m", "j", "t", "c", "s", "r", "u"], bucket.EnumerateObject().Select(p => p.Name));
        Assert.Equal(5, bucket.GetProperty("t").GetArrayLength());
        Assert.Equal(["provider", "offset", "mtime", "size", "state"], doc.RootElement.GetProperty("files").GetProperty("f").EnumerateObject().Select(p => p.Name));
        Directory.Delete(dir, true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"schema\":2,\"files\":{},\"buckets\":[]}")]
    [InlineData("[1,2]")]
    public void UnreadableOrForeignCachesLoadAsEmpty(string content)
    {
        var dir = T.TempDir();
        var path = Path.Combine(dir, "c.json");
        File.WriteAllText(path, content);
        var (files, buckets) = UsageCache.Load(path);
        Assert.Empty(files);
        Assert.Empty(buckets);
        Assert.Empty(UsageCache.Load(Path.Combine(dir, "missing.json")).Files);
        Directory.Delete(dir, true);
    }
}

public sealed class TurnTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    private static TurnTracker Tracker() => new() { ReportsTransitions = true };
    private static UsageDelta D(long output, double cost, int unpriced = 0) => new(true, new AgentTokens(0, 0, 0, output, 0), cost, unpriced);

    [Fact]
    public void FinishedCarriesDurationCostTokensAndProject()
    {
        var t = Tracker();
        t.Began("f", AgentProvider.Claude, Start);
        t.Context("f", "claude-opus-5-5", "demo");
        t.AddUsage("f", Start.AddMinutes(1), D(1000, 0.5), "claude-opus-5-5", "demo", subagent: false);
        t.AddUsage("f", Start.AddMinutes(2), D(500, 0.25), "claude-opus-5-5", "demo", subagent: false);
        var e = t.Ended("f", Start.AddMinutes(4).AddSeconds(12), true, null, Start, Start.AddMinutes(4).AddSeconds(14))!;
        Assert.Equal(AgentProvider.Claude, e.Provider);
        Assert.Equal(TimeSpan.FromSeconds(252), e.Duration);
        Assert.Equal(0.75, e.Cost);
        Assert.Equal(1500, e.Tokens);
        Assert.Equal("demo", e.Project);
        Assert.Empty(t.Live(Start.AddMinutes(5)));
    }

    [Fact]
    public void ReportedDurationWinsOverTimestamps()
    {
        var t = Tracker();
        t.Began("f", AgentProvider.Codex, Start);
        Assert.Equal(TimeSpan.FromSeconds(90), t.Ended("f", Start.AddSeconds(5), true, TimeSpan.FromSeconds(90), Start, Start.AddSeconds(6))!.Duration);
    }

    [Fact]
    public void InterruptedTurnsAndFirstPassHistoryNeverNotify()
    {
        var t = Tracker();
        t.Began("f", AgentProvider.Claude, Start);
        Assert.Null(t.Ended("f", Start.AddMinutes(2), completed: false, null, Start, Start.AddMinutes(2)));

        var history = new TurnTracker { ReportsTransitions = false };
        history.Began("f", AgentProvider.Claude, Start);
        Assert.Null(history.Ended("f", Start.AddMinutes(2), true, null, Start, Start.AddMinutes(2)));
        Assert.Null(Tracker().Ended("never-opened", Start, true, null, Start, Start));
    }

    [Fact]
    public void AnEndSeenMoreThanFiveMinutesLateIsHistoryNotNews()
    {
        var t = Tracker();
        t.Began("f", AgentProvider.Claude, Start);
        Assert.Null(t.Ended("f", Start.AddMinutes(1), true, null, Start, Start.AddMinutes(1).Add(TurnTracker.LateEnd).AddSeconds(1)));
        t.Began("g", AgentProvider.Claude, Start);
        Assert.NotNull(t.Ended("g", Start.AddMinutes(1), true, null, Start, Start.AddMinutes(1).Add(TurnTracker.LateEnd)));
    }

    [Fact]
    public void ATurnSilentForTenMinutesLeavesTheLiveListButResumes()
    {
        var t = Tracker();
        t.Began("f", AgentProvider.Claude, Start);
        t.Context("f", "m", "p");
        Assert.Single(t.Live(Start.AddMinutes(9).AddSeconds(59)));
        Assert.Empty(t.Live(Start.AddMinutes(10)));

        t.Active("f", AgentProvider.Claude, Start.AddMinutes(30), Start.AddMinutes(30));   // work resumes after an approval
        var live = Assert.Single(t.Live(Start.AddMinutes(31)));
        Assert.Equal(Start, live.Started);
        Assert.Equal(Start.AddMinutes(30), live.LastActivity);
        Assert.NotNull(t.Ended("f", Start.AddMinutes(31), true, null, Start, Start.AddMinutes(31)));   // the quiet turn ends as the whole turn
    }

    [Fact]
    public void SubagentUsageCountsTokensButLeavesModelAndProjectAlone()
    {
        var t = Tracker();
        t.Began("parent", AgentProvider.Claude, Start);
        t.Context("parent", "claude-opus-5-5", "main-proj");
        t.AddUsage("parent", Start.AddSeconds(30), D(40, 0.1), "claude-haiku-5-5", "other", subagent: true);
        var live = Assert.Single(t.Live(Start.AddMinutes(1)));
        Assert.Equal(40, live.Tokens.Total);
        Assert.Equal(0.1, live.Cost);
        Assert.Equal("claude-opus-5-5", live.Model);
        Assert.Equal("main-proj", live.Project);
    }

    [Fact]
    public void UsageBeforeTheTurnStartedIsNotCounted()
    {
        var t = Tracker();
        t.Began("f", AgentProvider.Claude, Start);
        t.AddUsage("f", Start.AddMinutes(-5), D(999, 9), "m", "p", false);
        Assert.Equal(0, t.Live(Start.AddSeconds(5))[0].Tokens.Total);
        t.AddUsage("nope", Start, D(1, 1), "m", "p", false);   // no such turn: ignored
    }

    [Fact]
    public void UnpricedOnlyTurnsReportNoCostAndRewrittenLogsKeepTheirTurn()
    {
        var t = Tracker();
        t.Began("f", AgentProvider.Claude, Start);
        t.AddUsage("f", Start.AddSeconds(1), D(10, 0, unpriced: 1), "m", "p", false);
        t.Began("f", AgentProvider.Claude, Start);   // the log was read again from its start: same turn, nothing lost
        Assert.Equal(10, t.Live(Start.AddSeconds(2))[0].Tokens.Total);
        Assert.Null(t.Ended("f", Start.AddSeconds(20), true, null, Start, Start.AddSeconds(20))!.Cost);
    }

    [Fact]
    public void ActiveWithoutBeganOpensATurnAtThatMoment()
    {
        var t = Tracker();
        t.Active("f", AgentProvider.Codex, null, Start);   // no timestamp on the line: the file's mtime stands in
        Assert.Equal(Start, Assert.Single(t.Live(Start.AddSeconds(1))).Started);
    }

    [Fact]
    public void ForgetAndPruneDropTurnsWithoutNotice()
    {
        var t = Tracker();
        t.Began("a", AgentProvider.Claude, Start);
        t.Began("b", AgentProvider.Codex, Start);
        t.Forget("a");
        Assert.Single(t.Live(Start.AddMinutes(1)));
        t.Prune(Start.AddHours(2));   // Codex turns wait up to six hours, Claude's one
        Assert.NotNull(t.Ended("b", Start.AddHours(2), true, null, Start, Start.AddHours(2)));
        t.Began("c", AgentProvider.Claude, Start);
        t.Prune(Start.AddHours(1).AddMinutes(1));
        Assert.Null(t.Ended("c", Start.AddHours(1), true, null, Start, Start.AddHours(1)));
    }

    [Fact]
    public void LiveIdsAreOpaque()
    {
        var t = Tracker();
        t.Began(@"C:\Users\someone\.claude\projects\x\sess.jsonl", AgentProvider.Claude, Start);
        var id = t.Live(Start.AddSeconds(1))[0].Id;
        Assert.DoesNotContain("someone", id);
        Assert.StartsWith("Claude:", id);
    }
}
