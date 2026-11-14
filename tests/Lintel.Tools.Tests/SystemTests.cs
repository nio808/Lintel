// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Tools.KeepAwake;
using Lintel.Tools.SystemStats;

namespace Lintel.Tools.Tests;

public sealed class SystemTests
{
    [Fact]
    public void CpuPercentIsBusyShareOfKernelPlusUser()
    {
        // kernel includes idle: 100 kernel (60 idle) + 4 user over the interval = 44 busy of 104.
        var p = SystemMath.CpuPercent(new CpuTimes(0, 0, 0), new CpuTimes(60, 100, 4));
        Assert.Equal(100.0 * 44 / 104, p, 6);
    }

    [Fact]
    public void CpuPercentHandlesNoElapsedTimeAndFullIdle()
    {
        var t = new CpuTimes(10, 20, 5);
        Assert.Equal(0, SystemMath.CpuPercent(t, t));
        Assert.Equal(0, SystemMath.CpuPercent(t, new CpuTimes(110, 120, 5)));
        Assert.Equal(100, SystemMath.CpuPercent(t, new CpuTimes(10, 70, 55)));
    }

    [Fact]
    public void RateIsBytesPerSecondAndNeverNegative()
    {
        Assert.Equal(500, SystemMath.Rate(1000, 2000, 2));
        Assert.Equal(0, SystemMath.Rate(5000, 100, 1));   // adapter reset
        Assert.Equal(0, SystemMath.Rate(0, 100, 0));
    }

    private sealed class Source : ISystemSource
    {
        public CpuTimes CpuNow { get; set; }
        public (long, long) Net { get; set; }
        public CpuTimes Cpu() => CpuNow;
        public (long Used, long Total) Memory() => (8, 16);
        public (long Received, long Sent) Network() => Net;
        public (long Free, long Total, string Name)? SystemDrive() => (1, 4, "C:");
        public PowerReading Power() => new(50, true);
        public TimeSpan Uptime() => TimeSpan.FromHours(3);
    }

    [Fact]
    public void SamplerReportsDeltasSincePreviousSample()
    {
        var time = new FakeTime();
        var source = new Source { CpuNow = new(0, 0, 0), Net = (1000, 100) };
        var sampler = new SystemSampler(source, time);
        sampler.Prime();
        time.Advance(TimeSpan.FromSeconds(2));
        source.CpuNow = new(50, 100, 0);
        source.Net = (5000, 500);
        var s = sampler.Sample();
        Assert.Equal(50, s.Cpu, 6);
        Assert.Equal(2000, s.NetDown, 6);
        Assert.Equal(200, s.NetUp, 6);
        Assert.Equal(50, s.MemPercent);
        Assert.Equal(1, s.DiskFree);

        time.Advance(TimeSpan.FromSeconds(1));
        source.Net = (5000, 500);   // nothing moved
        Assert.Equal(0, sampler.Sample().NetDown);
    }

    private static PowerReading Bat(int pct, bool onBattery = true) => new(pct, onBattery);

    [Fact]
    public void BatteryFirstReadingIsSilent()
    {
        var w = new BatteryWatcher();
        Assert.Equal(BatteryAlert.None, w.Update(Bat(15)));
        Assert.False(w.PlugChanged);
        Assert.Equal(BatteryAlert.None, w.Update(Bat(14)));
    }

    [Fact]
    public void BatteryAlertsOnceWhenCrossingEachLevel()
    {
        var w = new BatteryWatcher();
        w.Update(Bat(30));
        Assert.Equal(BatteryAlert.None, w.Update(Bat(21)));
        Assert.Equal(BatteryAlert.Low20, w.Update(Bat(20)));
        Assert.Equal(BatteryAlert.None, w.Update(Bat(19)));
        Assert.Equal(BatteryAlert.Low10, w.Update(Bat(10)));
        Assert.Equal(BatteryAlert.None, w.Update(Bat(9)));
    }

    [Fact]
    public void BatteryJumpingPastBothLevelsReportsTheLowerOne()
    {
        var w = new BatteryWatcher();
        w.Update(Bat(40));
        Assert.Equal(BatteryAlert.Low10, w.Update(Bat(8)));
        Assert.Equal(BatteryAlert.None, w.Update(Bat(7)));
    }

    [Fact]
    public void PluggingInRearmsAndNoAlertsWhileCharging()
    {
        var w = new BatteryWatcher();
        w.Update(Bat(30));
        Assert.Equal(BatteryAlert.Low20, w.Update(Bat(19)));
        Assert.Equal(BatteryAlert.None, w.Update(Bat(12, onBattery: false)));
        Assert.True(w.PlugChanged);
        Assert.Equal(BatteryAlert.Low20, w.Update(Bat(12)));   // unplugged below 20: warns again
        Assert.True(w.PlugChanged);
    }

    [Fact]
    public void RisingAboveALevelWhileOnBatteryRearmsIt()
    {
        var w = new BatteryWatcher();
        w.Update(Bat(30));
        Assert.Equal(BatteryAlert.Low20, w.Update(Bat(19)));
        w.Update(Bat(25));
        Assert.Equal(BatteryAlert.Low20, w.Update(Bat(18)));
    }

    [Fact]
    public void NoBatteryNeverAlerts()
    {
        var w = new BatteryWatcher();
        Assert.Equal(BatteryAlert.None, w.Update(new PowerReading(null, false, HasBattery: false)));
        Assert.Equal(BatteryAlert.None, w.Update(new PowerReading(null, false, HasBattery: false)));
    }
}
