// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Http;
using Lintel.Agents.Limits;
using Lintel.Core.Agents;

namespace Lintel.Agents.Tests;

public sealed class LimitsCoordinatorTests
{
    private static readonly AgentAccount Claude = new("claude:default", AgentProvider.Claude, "Claude", null, true);
    private static readonly AgentAccount Codex = new("codex:default", AgentProvider.Codex, "Codex", null, true);

    private sealed class Rig
    {
        public FakeTime Time = new(T.Now);
        public Queue<Func<AgentLimits>> Script = new();
        public int Reads;
        public bool CanRead = true;
        public AgentLimits? Fallback;
        public List<AgentLimits> Changes = [];
        public LimitsCoordinator Coordinator;

        public Rig(AgentAccount account)
        {
            Coordinator = new LimitsCoordinator(Time,
                (a, _) =>
                {
                    Reads++;
                    var step = Script.Count > 0 ? Script.Dequeue() : () => Reading(a, 10, Time.GetUtcNow());
                    return Task.FromResult(step());
                },
                _ => CanRead, _ => Fallback);
            Coordinator.Changed += Changes.Add;
            Coordinator.SetAccounts([account]);
        }

        public void Ok(AgentAccount a, double used) => Script.Enqueue(() => Reading(a, used, Time.GetUtcNow()));
        public void Fail(LimitFailure kind = LimitFailure.Other, TimeSpan? retry = null) =>
            Script.Enqueue(() => throw new LimitReadException("friendly failure text", kind, retry));
        public AgentLimits Only => Assert.Single(Coordinator.Current);
        public Task Tick(bool visible = false) => Coordinator.TickAsync(visible, CancellationToken.None);
    }

    private static AgentLimits Reading(AgentAccount a, double used, DateTimeOffset at, LimitSource source = LimitSource.Account) =>
        new(a.Id, a.Provider, [new AgentLimitWindow("w", LimitKind.Session, 300, null, "5-hour", used, at.AddHours(2))], at, source, null, null, false, false, null, null);

    [Fact]
    public async Task FirstTickReadsThenWaitsFiveMinutes()
    {
        var r = new Rig(Claude);
        await r.Tick();
        Assert.Equal(1, r.Reads);
        Assert.Equal(10, r.Only.Windows[0].UsedPercent);
        Assert.False(r.Only.IsStale);
        Assert.Equal(T.Now + BackoffPolicy.Interval, r.Only.NextAttempt);

        r.Time.Advance(TimeSpan.FromMinutes(4.9));
        await r.Tick();
        Assert.Equal(1, r.Reads);
        r.Time.Advance(TimeSpan.FromMinutes(0.2));
        await r.Tick();
        Assert.Equal(2, r.Reads);
    }

    [Fact]
    public async Task VisibleSectionRefreshesAReadingOlderThanSixtySeconds()
    {
        var r = new Rig(Claude);
        await r.Tick();
        r.Time.Advance(TimeSpan.FromSeconds(30));
        await r.Tick(visible: true);
        Assert.Equal(1, r.Reads);
        r.Time.Advance(TimeSpan.FromSeconds(31));
        await r.Tick(visible: false);
        Assert.Equal(1, r.Reads);
        await r.Tick(visible: true);
        Assert.Equal(2, r.Reads);
    }

    [Fact]
    public async Task FailureKeepsLastKnownReadingMarkedStaleWithTheFriendlyError()
    {
        var r = new Rig(Claude);
        r.Ok(Claude, 42);
        await r.Tick();
        r.Time.Advance(TimeSpan.FromMinutes(6));
        r.Fail();
        await r.Tick();

        Assert.Equal(42, r.Only.Windows[0].UsedPercent);
        Assert.True(r.Only.IsStale);
        Assert.Equal("friendly failure text", r.Only.Error);
        Assert.Equal(T.Now.AddMinutes(11), r.Only.NextAttempt);
    }

    [Fact]
    public async Task RecoveryClearsTheStaleFlag()
    {
        var r = new Rig(Claude);
        r.Fail();
        await r.Tick();
        Assert.True(r.Only.IsStale);
        Assert.Empty(r.Only.Windows);
        r.Time.Advance(TimeSpan.FromMinutes(5));
        r.Ok(Claude, 7);
        await r.Tick();
        Assert.False(r.Only.IsStale);
        Assert.Null(r.Only.Error);
        Assert.Equal(7, r.Only.Windows[0].UsedPercent);
    }

    [Fact]
    public async Task ConsecutiveFailuresDoubleUpToThirtyMinutes()
    {
        var r = new Rig(Codex);
        var expected = new[] { 5, 10, 20, 30, 30 };
        foreach (var minutes in expected)
        {
            r.Fail();
            await r.Tick();
            Assert.Equal(r.Time.GetUtcNow().AddMinutes(minutes), r.Only.NextAttempt);
            r.Time.Advance(TimeSpan.FromMinutes(minutes));
        }
    }

    [Fact]
    public async Task ManualRefreshAfterASignInFailureSkipsTheBackoff()
    {
        var r = new Rig(Claude);
        r.Fail(LimitFailure.Auth);
        await r.Tick();
        r.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await r.Coordinator.RefreshAsync(null, CancellationToken.None));   // the user may just have signed in
        Assert.Equal(2, r.Reads);
        Assert.False(r.Only.IsStale);
    }

    [Fact]
    public async Task RateLimitBackoffHonoursRetryAfterEvenOnManualRefresh()
    {
        var r = new Rig(Claude);
        r.Ok(Claude, 5);
        await r.Tick();
        r.Time.Advance(TimeSpan.FromMinutes(6));
        r.Fail(LimitFailure.RateLimited, TimeSpan.FromMinutes(40));
        await r.Tick();
        Assert.Equal(2, r.Reads);
        Assert.Equal(T.Now.AddMinutes(46), r.Only.NextAttempt);

        r.Time.Advance(TimeSpan.FromMinutes(20));
        Assert.False(await r.Coordinator.RefreshAsync(null, CancellationToken.None));   // back-off respected
        await r.Tick(visible: true);
        Assert.Equal(2, r.Reads);

        r.Time.Advance(TimeSpan.FromMinutes(21));
        Assert.True(await r.Coordinator.RefreshAsync("claude:default", CancellationToken.None));
        Assert.Equal(3, r.Reads);
    }

    [Fact]
    public async Task ManualRefreshAllowsOneSuccessfulReadPerThirtySeconds()
    {
        var r = new Rig(Claude);
        await r.Tick();
        r.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(await r.Coordinator.RefreshAsync(null, CancellationToken.None));
        r.Time.Advance(TimeSpan.FromSeconds(21));
        Assert.True(await r.Coordinator.RefreshAsync(null, CancellationToken.None));
        Assert.Equal(2, r.Reads);
    }

    [Fact]
    public async Task ClaudePrefersAccountReadingOverAnOlderAppSample()
    {
        var r = new Rig(Claude);
        r.Fallback = Reading(Claude, 99, T.Now.AddMinutes(-10), LimitSource.ClaudeApp);
        await r.Tick();
        Assert.Equal(LimitSource.Account, r.Only.Source);
        Assert.Equal(10, r.Only.Windows[0].UsedPercent);
    }

    [Fact]
    public async Task ClaudeUsesAFreshAppSampleWhenTheAccountFailsAndItIsNewer()
    {
        var r = new Rig(Claude);
        r.Ok(Claude, 10);
        await r.Tick();
        r.Time.Advance(TimeSpan.FromMinutes(6));
        r.Fail();
        r.Fallback = Reading(Claude, 33, r.Time.GetUtcNow().AddMinutes(-2), LimitSource.ClaudeApp);
        await r.Tick();
        Assert.Equal(LimitSource.ClaudeApp, r.Only.Source);
        Assert.Equal(33, r.Only.Windows[0].UsedPercent);
        Assert.False(r.Only.IsStale);
    }

    [Fact]
    public async Task ClaudeFallsBackToTheStaleAccountReadingWhenTheAppSampleIsOld()
    {
        var r = new Rig(Claude);
        r.Ok(Claude, 10);
        await r.Tick();
        r.Time.Advance(TimeSpan.FromMinutes(100));
        r.Fail();
        r.Fallback = Reading(Claude, 50, r.Time.GetUtcNow().AddMinutes(-45), LimitSource.ClaudeApp);   // newer than the account reading, but > 30 min old
        await r.Tick();
        Assert.Equal(LimitSource.Account, r.Only.Source);
        Assert.True(r.Only.IsStale);
    }

    [Fact]
    public async Task WithoutAccountConsentOnlyTheAppHistoryIsShownAndNothingIsScheduled()
    {
        var r = new Rig(Claude) { CanRead = false };
        r.Fallback = Reading(Claude, 20, T.Now.AddMinutes(-5), LimitSource.ClaudeApp);
        await r.Tick();
        Assert.Equal(0, r.Reads);
        Assert.Empty(r.Coordinator.Current);   // not composed until a recompose
        r.Coordinator.Recompose();
        Assert.Equal(LimitSource.ClaudeApp, r.Only.Source);
        Assert.Null(r.Only.NextAttempt);
        Assert.False(await r.Coordinator.RefreshAsync(null, CancellationToken.None));
        Assert.Equal(0, r.Reads);
    }

    [Fact]
    public async Task AnOldAppHistoryWithNothingElseIsFlaggedStale()
    {
        var r = new Rig(Claude) { CanRead = false };
        r.Fallback = Reading(Claude, 20, T.Now.AddHours(-3), LimitSource.ClaudeApp);
        r.Coordinator.Recompose();
        Assert.True(r.Only.IsStale);
        Assert.NotNull(r.Only.Error);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CodexShowsTheNewerOfAccountAndSessionLog()
    {
        var r = new Rig(Codex);
        r.Fallback = Reading(Codex, 77, T.Now.AddMinutes(1), LimitSource.SessionLog);
        await r.Tick();                                    // account reading is stamped T.Now, the log reading is newer
        Assert.Equal(LimitSource.SessionLog, r.Only.Source);
        Assert.Equal(77, r.Only.Windows[0].UsedPercent);
        Assert.False(r.Only.IsStale);

        r.Fallback = Reading(Codex, 3, T.Now.AddHours(-1), LimitSource.SessionLog);
        r.Coordinator.Recompose();
        Assert.Equal(LimitSource.Account, r.Only.Source);
    }

    [Fact]
    public async Task CodexAccountFailureWithAFreshLogReadingIsNotStale()
    {
        var r = new Rig(Codex);
        r.Fail();
        await r.Tick();                                    // fails at T.Now
        r.Time.Advance(TimeSpan.FromMinutes(1));
        r.Fallback = Reading(Codex, 12, r.Time.GetUtcNow(), LimitSource.SessionLog);
        r.Coordinator.Recompose();
        Assert.False(r.Only.IsStale);
        Assert.Equal(12, r.Only.Windows[0].UsedPercent);
    }

    [Fact]
    public async Task OnlyRealChangesRaiseChanged()
    {
        var r = new Rig(Claude);
        await r.Tick();
        var count = r.Changes.Count;
        r.Coordinator.Recompose();
        r.Coordinator.Recompose();
        Assert.Equal(count, r.Changes.Count);
    }

    [Fact]
    public async Task RemovedAccountsDisappear()
    {
        var r = new Rig(Claude);
        await r.Tick();
        r.Coordinator.SetAccounts([]);
        Assert.Empty(r.Coordinator.Current);
    }

    [Fact]
    public async Task ErrorTextNeverCarriesTheUnderlyingExceptionMessage()
    {
        var r = new Rig(Claude);
        r.Script.Enqueue(() => throw new HttpRequestException("Bearer sk-ant-oat-SECRET"));
        await r.Tick();
        Assert.DoesNotContain("SECRET", r.Only.Error);
        Assert.Equal("Offline or network unavailable. Your last reading is kept.", r.Only.Error);
    }
}
