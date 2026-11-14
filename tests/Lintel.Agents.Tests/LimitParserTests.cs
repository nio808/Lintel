// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text;
using System.Text.Json;
using Lintel.Agents.Limits;
using Lintel.Agents.Pricing;
using Lintel.Core.Agents;

namespace Lintel.Agents.Tests;

public sealed class ClaudeUsageParserTests
{
    private static readonly PriceList Prices = PriceSource.Embedded();

    [Fact]
    public void FourWindowsAreParsedWithLabelsKindsAndScopes()
    {
        var plan = AgentPlans.Claude("max", null, Prices);
        var l = ClaudeUsageParser.Parse(T.Fixture("claude-usage.json"), plan, T.Now);

        Assert.Equal(["claude.five_hour", "claude.seven_day", "claude.seven_day_sonnet", "claude.seven_day_opus"], l.Windows.Select(w => w.Id));
        Assert.Equal(["5-hour", "Weekly", "Weekly · Sonnet", "Weekly · Opus"], l.Windows.Select(w => w.Label));
        Assert.Equal([LimitKind.Session, LimitKind.Weekly, LimitKind.Weekly, LimitKind.Weekly], l.Windows.Select(w => w.Kind));
        Assert.Equal([null, null, "Sonnet", "Opus"], l.Windows.Select(w => w.Scope));
        Assert.Equal(37.5, l.Windows[0].UsedPercent);
        Assert.Equal(62.5, l.Windows[0].RemainingPercent);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 18, 0, 0, TimeSpan.Zero), l.Windows[0].ResetsAt);
        Assert.Null(l.Windows[3].ResetsAt);
        Assert.Equal(LimitSource.Account, l.Source);
        Assert.Equal("Max", l.Plan?.Name);
        Assert.False(l.IsStale);
        Assert.Null(l.Error);
    }

    [Fact]
    public void BalanceBecomesCreditsLine()
    {
        var l = ClaudeUsageParser.Parse(T.Fixture("claude-usage.json"), null, T.Now);
        Assert.Equal("Credits · $12.50", l.Credits);
    }

    [Fact]
    public void LimitsArrayIsTheFallbackWhenNoNamedWindowExists()
    {
        var l = ClaudeUsageParser.Parse(T.Fixture("claude-usage-limits-array.json"), null, T.Now);
        Assert.Equal(["5-hour", "Weekly", "weekly_opus"], l.Windows.Select(w => w.Label));
        Assert.Equal(42, l.Windows[0].UsedPercent);
        Assert.Equal(300, l.Windows[0].Minutes);
        Assert.Equal(10080, l.Windows[1].Minutes);
        Assert.Null(l.Windows[2].Minutes);
        Assert.Equal("Extra usage off", l.Credits);
    }

    [Fact]
    public void CapIsNeverShownAsAWalletBalance()
    {
        // extra_usage.monthly_limit / used_credits exist in the payload but only the explicit balance is shown.
        var json = """{"five_hour":{"utilization":1},"extra_usage":{"is_enabled":true,"monthly_limit":5000,"used_credits":10}}""";
        Assert.Equal("Extra usage on · balance not reported", ClaudeUsageParser.Parse(json, null, T.Now).Credits);
    }

    [Fact]
    public void OutOfCreditsAndBlocked()
    {
        var l = ClaudeUsageParser.Parse(T.Fixture("claude-usage-out-of-credits.json"), null, T.Now);
        Assert.Equal("Extra usage · out of credits", l.Credits);
        Assert.True(l.Blocked);
    }

    [Theory]
    [InlineData("claude-usage-empty.json")]
    public void NoWindowsIsAnError(string fixture) =>
        Assert.Throws<InvalidDataException>(() => ClaudeUsageParser.Parse(T.Fixture(fixture), null, T.Now));

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    public void NonObjectPayloadIsAnError(string json) =>
        Assert.Throws<InvalidDataException>(() => ClaudeUsageParser.Parse(json, null, T.Now));

    [Fact]
    public void GarbageIsAJsonError() =>
        Assert.ThrowsAny<JsonException>(() => ClaudeUsageParser.Parse("<html>", null, T.Now));

    [Fact]
    public void NumericStringsAndOutOfRangeValues()
    {
        var json = """{"five_hour":{"utilization":"12.5"},"seven_day":{"utilization":-3},"seven_day_opus":{"utilization":99999}}""";
        var l = ClaudeUsageParser.Parse(json, null, T.Now);
        Assert.Single(l.Windows);
        Assert.Equal(12.5, l.Windows[0].UsedPercent);
    }
}

public sealed class CodexUsageParserTests
{
    private static readonly PriceList Prices = PriceSource.Embedded();
    private static AgentLimits Parse(string fixture) => CodexUsageParser.Parse(T.Fixture(fixture), "codex:default", Prices, T.Now);

    [Fact]
    public void SingleRateLimits()
    {
        var l = Parse("codex-single.json");
        Assert.Equal(["5-hour", "Weekly"], l.Windows.Select(w => w.Label));
        Assert.Equal(["codex.300", "codex.10080"], l.Windows.Select(w => w.Id));
        Assert.Equal([LimitKind.Session, LimitKind.Weekly], l.Windows.Select(w => w.Kind));
        Assert.Equal(15, l.Windows[0].UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794232800), l.Windows[0].ResetsAt);
        Assert.Equal("Plus", l.Plan?.Name);
        Assert.Equal(20, l.Plan?.MonthlyPrice);
        Assert.Equal("No extra credits", l.Credits);
        Assert.False(l.Blocked);
        Assert.Equal(LimitSource.Account, l.Source);
        Assert.Equal("codex:default", l.AccountId);
    }

    [Fact]
    public void ByLimitIdKeepsMainFirstAndNamesOtherBuckets()
    {
        var l = Parse("codex-by-limit-id.json");
        Assert.Equal(["5-hour", "Weekly", "GPT-6 Sol · 5-hour"], l.Windows.Select(w => w.Label));
        Assert.Equal("GPT-6 Sol", l.Windows[2].Scope);
        Assert.Equal("codex.codex_other.300", l.Windows[2].Id);
        Assert.Equal("Credits · unlimited", l.Credits);   // the main bucket's credits win
        Assert.Equal("Pro", l.Plan?.Name);
    }

    [Fact]
    public void WeeklyPrimaryWindowIsLabelledWeekly()
    {
        var l = Parse("codex-weekly-primary.json");
        var w = Assert.Single(l.Windows);
        Assert.Equal("Weekly", w.Label);
        Assert.Equal(LimitKind.Weekly, w.Kind);
        Assert.Equal("Credit balance not reported", l.Credits);
    }

    [Fact]
    public void BlockedFlags()
    {
        Assert.True(Parse("codex-blocked.json").Blocked);
        var spend = Parse("codex-blocked-spend.json");
        Assert.True(spend.Blocked);
        Assert.Equal("Credits · 3.00", spend.Credits);
        Assert.Equal("1-hour", spend.Windows[0].Label);
    }

    [Fact]
    public void NoWindowsIsAnError() =>
        Assert.Throws<InvalidDataException>(() => Parse("codex-empty.json"));

    [Theory]
    [InlineData(300, "5-hour")]
    [InlineData(10080, "Weekly")]
    [InlineData(1440, "Daily")]
    [InlineData(4320, "3-day")]
    [InlineData(60, "1-hour")]
    [InlineData(90, "90-minute")]
    [InlineData(null, "Quota")]
    [InlineData(0, "Quota")]
    public void WindowLabels(int? minutes, string expected) => Assert.Equal(expected, CodexUsageParser.WindowLabel(minutes));
}

public sealed class ClaudeAppHistoryTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 10, 20, 0, TimeSpan.Zero);

    private static AgentLimits? Parse(string fixture, DateTimeOffset now) =>
        ClaudeAppHistory.Parse(Encoding.UTF8.GetBytes(T.Fixture(fixture)), now);

    [Fact]
    public void Version2TakesTheNewestValuePerWindowAndHasNoResetTimes()
    {
        var l = Parse("app-history-v2.json", At)!;
        Assert.Equal(LimitSource.ClaudeApp, l.Source);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 10, 15, 0, TimeSpan.Zero), l.ObservedAt);
        Assert.Equal(["claude.five_hour", "claude.seven_day", "claude.seven_day_sonnet", "claude.seven_day_opus"], l.Windows.Select(w => w.Id));
        Assert.Equal([14, 51, 21, 3], l.Windows.Select(w => w.UsedPercent));   // "so" only appears in the first sample
        Assert.All(l.Windows, w => Assert.Null(w.ResetsAt));
        Assert.Equal("Reset time not reported", Lintel.Core.Text.Format.Countdown(l.Windows[0].ResetsAt, At));
        Assert.Equal("Opus", l.Windows[3].Scope);
    }

    [Fact]
    public void Version1KeepsWindowsBesideTheTime()
    {
        var l = Parse("app-history-v1.json", At)!;
        Assert.Equal([33, 44], l.Windows.Select(w => w.UsedPercent));
    }

    [Fact]
    public void SessionSampleOlderThanFiveHoursIsDropped()
    {
        var l = Parse("app-history-v2.json", At.AddHours(6))!;
        Assert.DoesNotContain(l.Windows, w => w.Id == "claude.five_hour");
        Assert.Contains(l.Windows, w => w.Id == "claude.seven_day");
    }

    [Fact]
    public void SamplesFromTheFutureAreIgnoredAndStalenessIsTheCallers()
    {
        Assert.Null(Parse("app-history-v2.json", At.AddDays(-1)));
        var old = Parse("app-history-v2.json", At.AddMinutes(45))!;
        Assert.True(At.AddMinutes(45) - old.ObservedAt > ClaudeAppHistory.Freshness);   // LimitsCoordinator treats it as stale
    }

    [Fact]
    public void UnknownVersionOrGarbageIsRejected()
    {
        Assert.Null(Parse("app-history-unknown-version.json", At));
        Assert.Null(ClaudeAppHistory.Parse("not json"u8, At));
        Assert.Null(ClaudeAppHistory.Parse("""{"version":2,"samples":[{"t":1,"u":{}}]}"""u8, At));
    }
}
