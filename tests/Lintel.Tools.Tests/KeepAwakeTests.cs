// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Tools.KeepAwake;

namespace Lintel.Tools.Tests;

public sealed class KeepAwakeTests
{
    private readonly FakeTime time = new();
    private static readonly PowerReading Ac = new(100, false);

    [Fact]
    public void TimedSessionCountsDownAndExpires()
    {
        var s = KeepAwakeSession.Timed(time.Now, TimeSpan.FromMinutes(15), true, true);
        Assert.Equal(TimeSpan.FromMinutes(15), s.Remaining(time.Now));
        time.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(TimeSpan.FromMinutes(1), s.Remaining(time.Now));
        Assert.Null(s.Check(time.Now, Ac, true));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(KeepAwakeEnd.Expired, s.Check(time.Now, Ac, true));
        Assert.Equal(TimeSpan.Zero, s.Remaining(time.Now));
    }

    [Fact]
    public void IndefiniteNeverExpiresAndHasNoRemaining()
    {
        var s = KeepAwakeSession.Indefinite(true, true);
        time.Advance(TimeSpan.FromDays(30));
        Assert.Null(s.Remaining(time.Now));
        Assert.Null(s.Check(time.Now, Ac, true));
    }

    [Fact]
    public void WhileAppEndsWhenProcessIsGone()
    {
        var s = KeepAwakeSession.WhileApp("notepad", true, true);
        Assert.Null(s.Check(time.Now, Ac, appRunning: true));
        Assert.Equal(KeepAwakeEnd.AppExited, s.Check(time.Now, Ac, appRunning: false));
    }

    [Theory]
    [InlineData(19, true, true, KeepAwakeEnd.LowBattery)]
    [InlineData(20, true, true, null)]       // "below 20%"
    [InlineData(5, false, true, null)]       // on AC
    [InlineData(5, true, false, null)]       // option off
    public void AutoOffBelowTwentyPercentOnBatteryOnly(int percent, bool onBattery, bool option, KeepAwakeEnd? expected)
    {
        var s = KeepAwakeSession.Indefinite(true, option);
        Assert.Equal(expected, s.Check(time.Now, new PowerReading(percent, onBattery), true));
    }

    [Fact]
    public void DesktopWithoutBatteryIsNotLowBattery()
    {
        var s = KeepAwakeSession.Indefinite(true, true);
        Assert.Null(s.Check(time.Now, new PowerReading(null, false, HasBattery: false), true));
    }

    [Fact]
    public void WithOptionsKeepsTheEndTime()
    {
        var s = KeepAwakeSession.Timed(time.Now, TimeSpan.FromHours(1), true, true).WithOptions(false, false);
        Assert.False(s.KeepDisplay);
        Assert.False(s.AutoOffOnLowBattery);
        Assert.Equal(TimeSpan.FromHours(1), s.Remaining(time.Now));
    }
}
