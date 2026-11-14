// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Tools.Timer;

namespace Lintel.Tools.Tests;

public sealed class TimerEngineTests
{
    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);
    private readonly FakeTime time = new();
    private readonly List<TimerCompletion> done = [];

    private TimerEngine Engine()
    {
        var e = new TimerEngine(time);
        e.Completed += done.Add;
        return e;
    }

    [Fact]
    public void CountdownRunsThenFinishesOnce()
    {
        var e = Engine();
        e.StartCountdown(5 * Min);
        Assert.Equal(TimerState.Running, e.State);
        time.Advance(2 * Min);
        e.Tick();
        Assert.Equal(3 * Min, e.Remaining);
        Assert.Equal(0.4, e.Progress, 3);
        Assert.Empty(done);
        time.Advance(3 * Min);
        e.Tick();
        e.Tick();
        Assert.Equal(TimerState.Finished, e.State);
        var c = Assert.Single(done);
        Assert.Equal(5 * Min, c.Length);
        Assert.False(c.Overdue);
    }

    [Fact]
    public void PauseFreezesAndResumeContinues()
    {
        var e = Engine();
        e.StartCountdown(10 * Min);
        time.Advance(4 * Min);
        e.Pause();
        time.Advance(30 * Min);
        e.Tick();
        Assert.Equal(TimerState.Paused, e.State);
        Assert.Equal(6 * Min, e.Remaining);
        e.Resume();
        time.Advance(6 * Min);
        e.Tick();
        Assert.Equal(TimerState.Finished, e.State);
    }

    [Fact]
    public void AddTimeExtendsRunningPausedAndRestartsFinished()
    {
        var e = Engine();
        e.StartCountdown(Min);
        e.AddTime(Min);
        Assert.Equal(2 * Min, e.Remaining);
        Assert.Equal(2 * Min, e.Total);
        e.Pause();
        e.AddTime(Min);
        Assert.Equal(3 * Min, e.Remaining);
        e.Resume();
        time.Advance(4 * Min);
        e.Tick();
        Assert.Equal(TimerState.Finished, e.State);
        e.AddTime(Min);
        Assert.Equal(TimerState.Running, e.State);
        Assert.Equal(Min, e.Remaining);
    }

    [Fact]
    public void CancelReturnsToIdle()
    {
        var e = Engine();
        e.StartCountdown(Min);
        e.Cancel();
        Assert.Equal(TimerState.Idle, e.State);
        time.Advance(2 * Min);
        e.Tick();
        Assert.Empty(done);
    }

    [Fact]
    public void StartRejectsZeroAndOverMax()
    {
        var e = Engine();
        Assert.Throws<ArgumentOutOfRangeException>(() => e.StartCountdown(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => e.StartCountdown(TimerEngine.MaxCustom + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void StopwatchCountsLapsAndPauses()
    {
        var e = Engine();
        e.StartStopwatch();
        time.Advance(TimeSpan.FromSeconds(75));
        e.Lap();
        e.Pause();
        time.Advance(10 * Min);
        e.Resume();
        time.Advance(TimeSpan.FromSeconds(5));
        e.Lap();
        Assert.Equal(TimeSpan.FromSeconds(80), e.Elapsed);
        Assert.Equal([TimeSpan.FromSeconds(75), TimeSpan.FromSeconds(80)], e.Laps);
        Assert.Equal(20.0 / 60, e.Progress, 3);
        e.Tick();
        Assert.Empty(done);
        e.Cancel();
        Assert.Empty(e.Laps);
    }

    [Fact]
    public void PomodoroRunsFullCycle()
    {
        var e = Engine();
        e.StartPomodoro();
        var phases = new List<(PomodoroPhase Phase, int Round)> { (e.Phase, e.Round) };
        for (var i = 0; i < 20 && e.State == TimerState.Running; i++)
        {
            time.Advance(e.Remaining);
            e.Tick();
            if (e.State == TimerState.Running)
                phases.Add((e.Phase, e.Round));
        }
        Assert.Equal(
        [
            (PomodoroPhase.Focus, 1), (PomodoroPhase.ShortBreak, 1), (PomodoroPhase.Focus, 2), (PomodoroPhase.ShortBreak, 2),
            (PomodoroPhase.Focus, 3), (PomodoroPhase.ShortBreak, 3), (PomodoroPhase.Focus, 4), (PomodoroPhase.LongBreak, 4),
        ], phases);
        Assert.Equal(TimerState.Finished, e.State);
        Assert.Equal(8, done.Count);
        Assert.All(done.Take(7), c => Assert.NotNull(c.Next));
        Assert.Null(done[^1].Next);
        Assert.Equal(TimeSpan.FromMinutes(15), done[^1].Length);
    }

    [Fact]
    public void PomodoroPhaseStartsAtPreviousEndWithoutDrift()
    {
        var e = Engine();
        e.StartPomodoro();
        time.Advance(TimeSpan.FromMinutes(25) + TimeSpan.FromSeconds(1));   // late by 1 s
        e.Tick();
        Assert.Equal(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1), e.Remaining);
    }

    [Theory]
    [InlineData("25", 25 * 60)]
    [InlineData(" 90 ", 90 * 60)]
    [InlineData("1:30", 90)]
    [InlineData("1:02:03", 3723)]
    [InlineData("23:59:59", 86399)]
    public void ParsesDurations(string text, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), TimerEngine.ParseDuration(text));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("0:00")]
    [InlineData("1:75")]
    [InlineData("24:00:00")]
    [InlineData("1:2:3:4")]
    [InlineData("-5")]
    [InlineData("1,5")]
    public void RejectsBadDurations(string text) => Assert.Null(TimerEngine.ParseDuration(text));

    [Fact]
    public void SnapshotRestoresRunningTimer()
    {
        var a = Engine();
        a.StartCountdown(10 * Min);
        time.Advance(3 * Min);
        var snap = a.Snapshot();

        time.Advance(Min);   // "restart" a minute later
        var b = Engine();
        b.Restore(snap);
        Assert.Equal(TimerState.Running, b.State);
        Assert.Equal(6 * Min, b.Remaining);
        Assert.Empty(done);
    }

    [Fact]
    public void SnapshotRestoresPausedTimerExactly()
    {
        var a = Engine();
        a.StartCountdown(10 * Min);
        time.Advance(3 * Min);
        a.Pause();
        var snap = a.Snapshot();
        time.Advance(TimeSpan.FromHours(5));
        var b = Engine();
        b.Restore(snap);
        Assert.Equal(TimerState.Paused, b.State);
        Assert.Equal(7 * Min, b.Remaining);
    }

    [Fact]
    public void OverdueRestoreFiresImmediately()
    {
        var a = Engine();
        a.StartCountdown(10 * Min);
        var snap = a.Snapshot();
        time.Advance(TimeSpan.FromHours(1));
        var b = Engine();
        b.Restore(snap);
        Assert.Equal(TimerState.Finished, b.State);
        Assert.True(Assert.Single(done).Overdue);
    }

    [Fact]
    public void OverduePomodoroRestartsNextPhaseFromNow()
    {
        var a = Engine();
        a.StartPomodoro();
        var snap = a.Snapshot();
        time.Advance(TimeSpan.FromHours(2));
        var b = Engine();
        b.Restore(snap);
        Assert.Equal(PomodoroPhase.ShortBreak, b.Phase);
        Assert.Equal(TimeSpan.FromMinutes(5), b.Remaining);
        Assert.True(Assert.Single(done).Overdue);
    }
}
