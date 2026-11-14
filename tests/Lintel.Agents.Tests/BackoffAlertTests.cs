// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Http;
using Lintel.Agents.Limits;
using Lintel.Core.Agents;

namespace Lintel.Agents.Tests;

public sealed class BackoffPolicyTests
{
    private static TimeSpan M(double minutes) => TimeSpan.FromMinutes(minutes);

    [Fact]
    public void RateLimitedUsesRetryAfterWithAFiveMinuteFloorAndFifteenByDefault()
    {
        Assert.Equal(M(15), BackoffPolicy.Delay(LimitFailure.RateLimited, 1, null));
        Assert.Equal(M(5), BackoffPolicy.Delay(LimitFailure.RateLimited, 1, TimeSpan.FromSeconds(30)));   // delta below the floor
        Assert.Equal(M(40), BackoffPolicy.Delay(LimitFailure.RateLimited, 1, M(40)));                    // delta / date above it
        Assert.Equal(M(5), BackoffPolicy.Delay(LimitFailure.RateLimited, 1, TimeSpan.FromMinutes(-2)));  // a date already past
    }

    [Fact]
    public void AuthFailuresWaitFifteenMinutes()
    {
        Assert.Equal(M(15), BackoffPolicy.Delay(LimitFailure.Auth, 1));
        Assert.Equal(M(15), BackoffPolicy.Delay(LimitFailure.Auth, 9));
    }

    [Fact]
    public void OtherFailuresDoubleFromFiveMinutesAndCapAtThirty()
    {
        Assert.Equal(M(5), BackoffPolicy.Delay(LimitFailure.Other, 1));
        Assert.Equal(M(10), BackoffPolicy.Delay(LimitFailure.Other, 2));
        Assert.Equal(M(20), BackoffPolicy.Delay(LimitFailure.Other, 3));
        Assert.Equal(M(30), BackoffPolicy.Delay(LimitFailure.Other, 4));
        Assert.Equal(M(30), BackoffPolicy.Delay(LimitFailure.Other, 40));
    }

    [Fact]
    public void FriendlyErrorsUseFixedWording()
    {
        Assert.Equal("Offline or network unavailable. Your last reading is kept.", BackoffPolicy.Friendly(new HttpRequestException("x")));
        Assert.Equal("Usage check timed out. Will try again automatically.", BackoffPolicy.Friendly(new TaskCanceledException()));
        Assert.Equal("The service returned no readable subscription quotas. Try your CLI's usage screen.", BackoffPolicy.Friendly(new System.IO.InvalidDataException()));
        Assert.Equal("Could not read local login files. Check access and try again.", BackoffPolicy.Friendly(new UnauthorizedAccessException()));
        Assert.Equal("Usage check failed. Will try again automatically.", BackoffPolicy.Friendly(new Exception("secret-token-123")));
    }
}

public sealed class AlertTrackerTests
{
    private static readonly double[] Thresholds = [20, 5];

    private static AgentLimits Reading(double usedFiveHour, DateTimeOffset? resets = null, bool stale = false, string account = "claude:default") =>
        new(account, AgentProvider.Claude,
            [new AgentLimitWindow("claude.five_hour", LimitKind.Session, 300, null, "5-hour", usedFiveHour, resets ?? T.Now.AddHours(3))],
            T.Now, LimitSource.Account, null, null, false, stale, null, null);

    private static List<AgentUsageEvent> Eval(AlertTracker t, AgentLimits l, bool onReset = true) => t.Evaluate(l, Thresholds, true, onReset);

    [Fact]
    public void NeverAlertsOnTheFirstReadingEvenWhenAlreadyLow()
    {
        var t = new AlertTracker();
        Assert.Empty(Eval(t, Reading(97)));
        Assert.Empty(Eval(t, Reading(97.5)));   // 20 and 5 are already armed as crossed
    }

    [Fact]
    public void OneWarningPerThresholdInOrder()
    {
        var t = new AlertTracker();
        Assert.Empty(Eval(t, Reading(50)));
        Assert.Empty(Eval(t, Reading(79)));      // 21% left: not below 20
        var w20 = Assert.IsType<AgentUsageEvent.LimitWarning>(Assert.Single(Eval(t, Reading(81))));
        Assert.Equal(20, w20.ThresholdRemaining);
        Assert.Equal("claude.five_hour", w20.Window.Id);
        Assert.Empty(Eval(t, Reading(85)));      // still between thresholds: nothing new
        var w5 = Assert.IsType<AgentUsageEvent.LimitWarning>(Assert.Single(Eval(t, Reading(96))));
        Assert.Equal(5, w5.ThresholdRemaining);
        Assert.Empty(Eval(t, Reading(99)));      // already warned at both
    }

    [Fact]
    public void CrossingBothAtOnceWarnsOnlyForTheLowest()
    {
        var t = new AlertTracker();
        Eval(t, Reading(10));
        var e = Assert.IsType<AgentUsageEvent.LimitWarning>(Assert.Single(Eval(t, Reading(97))));
        Assert.Equal(5, e.ThresholdRemaining);
        Assert.Empty(Eval(t, Reading(98)));
    }

    [Fact]
    public void ResetRearmsAndOptionallyNotifies()
    {
        var t = new AlertTracker();
        Eval(t, Reading(10));
        Assert.Single(Eval(t, Reading(85)));                       // warned at 20
        var reset = Assert.Single(Eval(t, Reading(2)));            // usage dropped: window reset
        Assert.IsType<AgentUsageEvent.LimitReset>(reset);
        var again = Assert.IsType<AgentUsageEvent.LimitWarning>(Assert.Single(Eval(t, Reading(82))));   // re-armed
        Assert.Equal(20, again.ThresholdRemaining);
    }

    [Fact]
    public void ResetNoticeCanBeSwitchedOffAndNeedsAPriorWarning()
    {
        var t = new AlertTracker();
        Eval(t, Reading(10));
        Assert.Empty(Eval(t, Reading(2)));                         // never warned: no reset notice
        Eval(t, Reading(85));
        Assert.Empty(Eval(t, Reading(1), onReset: false));
    }

    [Fact]
    public void ARolledOverResetTimeAlsoCountsAsAReset()
    {
        var t = new AlertTracker();
        Eval(t, Reading(10, T.Now.AddHours(1)));
        Eval(t, Reading(85, T.Now.AddHours(1)));
        var events = Eval(t, Reading(86, T.Now.AddHours(6)));      // same usage, next period's reset time
        Assert.IsType<AgentUsageEvent.LimitReset>(Assert.Single(events));
    }

    [Fact]
    public void StaleReadingsSayNothingAndAccountsAreIndependent()
    {
        var t = new AlertTracker();
        Eval(t, Reading(10));
        Assert.Empty(Eval(t, Reading(99, stale: true)));
        Assert.Empty(Eval(t, Reading(99, account: "codex:default")));   // first reading of another account
        Assert.Single(Eval(t, Reading(99)));
    }

    [Fact]
    public void DisabledWarningsStillArmTheThresholds()
    {
        var t = new AlertTracker();
        t.Evaluate(Reading(10), Thresholds, true, true);
        Assert.Empty(t.Evaluate(Reading(85), Thresholds, warn: false, onReset: true));
        Assert.Empty(t.Evaluate(Reading(86), Thresholds, warn: true, onReset: true));   // no late alert for the old crossing
    }
}
