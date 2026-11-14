// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Net.NetworkInformation;
using Lintel.Tools.KeepAwake;

namespace Lintel.Tools.SystemStats;

public readonly record struct CpuTimes(long Idle, long Kernel, long User);

public sealed record SystemSample(
    double Cpu, long MemUsed, long MemTotal, double NetDown, double NetUp,
    long DiskFree, long DiskTotal, string DiskName, PowerReading Power, TimeSpan Uptime)
{
    public double MemPercent => MemTotal > 0 ? 100.0 * MemUsed / MemTotal : 0;
}

/// <summary>Raw OS numbers; faked in tests and Preview demo.</summary>
public interface ISystemSource
{
    CpuTimes Cpu();
    (long Used, long Total) Memory();
    (long Received, long Sent) Network();
    (long Free, long Total, string Name)? SystemDrive();
    PowerReading Power();
    TimeSpan Uptime();
}

public static class SystemMath
{
    /// <summary>Busy percent between two GetSystemTimes readings (Kernel includes Idle).</summary>
    public static double CpuPercent(CpuTimes prev, CpuTimes cur)
    {
        var total = (cur.Kernel - prev.Kernel) + (cur.User - prev.User);
        if (total <= 0)
            return 0;
        var busy = total - (cur.Idle - prev.Idle);
        return Math.Clamp(100.0 * busy / total, 0, 100);
    }

    /// <summary>Bytes per second; a counter that went backwards (adapter removed/reset) counts as 0.</summary>
    public static double Rate(long prev, long cur, double seconds) => seconds <= 0 || cur < prev ? 0 : (cur - prev) / seconds;
}

/// <summary>Takes a sample each call; CPU and network are deltas against the previous call (or Prime).</summary>
public sealed class SystemSampler(ISystemSource source, TimeProvider time)
{
    private CpuTimes prevCpu;
    private (long Rx, long Tx) prevNet;
    private long prevStamp;
    private bool primed;

    /// <summary>Resets the delta baseline (call when sampling resumes after a pause).</summary>
    public void Prime()
    {
        prevCpu = source.Cpu();
        prevNet = source.Network();
        prevStamp = time.GetTimestamp();
        primed = true;
    }

    public SystemSample Sample()
    {
        if (!primed)
            Prime();
        var cpu = source.Cpu();
        var net = source.Network();
        var now = time.GetTimestamp();
        var seconds = time.GetElapsedTime(prevStamp, now).TotalSeconds;
        var memory = source.Memory();
        var drive = source.SystemDrive();
        var sample = new SystemSample(
            SystemMath.CpuPercent(prevCpu, cpu), memory.Used, memory.Total,
            SystemMath.Rate(prevNet.Rx, net.Received, seconds), SystemMath.Rate(prevNet.Tx, net.Sent, seconds),
            drive?.Free ?? 0, drive?.Total ?? 0, drive?.Name ?? "", source.Power(), source.Uptime());
        prevCpu = cpu;
        prevNet = net;
        prevStamp = now;
        return sample;
    }
}

public sealed class WindowsSystemSource : ISystemSource
{
    public CpuTimes Cpu() => Native.GetSystemTimes(out var idle, out var kernel, out var user) ? new(idle, kernel, user) : default;

    public (long Used, long Total) Memory()
    {
        var status = new Native.MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MemoryStatusEx>() };
        return Native.GlobalMemoryStatusEx(ref status) ? ((long)(status.TotalPhys - status.AvailPhys), (long)status.TotalPhys) : (0, 0);
    }

    public (long Received, long Sent) Network()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                var stats = nic.GetIPStatistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }
        }
        catch (NetworkInformationException)
        {
        }
        return (rx, tx);
    }

    public (long Free, long Total, string Name)? SystemDrive()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (root is null)
                return null;
            var drive = new DriveInfo(root);
            return (drive.AvailableFreeSpace, drive.TotalSize, drive.Name.TrimEnd('\\'));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public PowerReading Power()
    {
        if (!Native.GetSystemPowerStatus(out var s))
            return new PowerReading(null, false, HasBattery: false);
        var hasBattery = (s.BatteryFlag & 128) == 0 && s.BatteryFlag != 255;
        int? percent = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null;
        TimeSpan? remaining = s.BatteryLifeTime != uint.MaxValue ? TimeSpan.FromSeconds(s.BatteryLifeTime) : null;
        return new PowerReading(hasBattery ? percent : null, hasBattery && s.AcLineStatus == 0, (s.BatteryFlag & 8) != 0, hasBattery, remaining);
    }

    public TimeSpan Uptime() => TimeSpan.FromMilliseconds(Environment.TickCount64);
}

/// <summary>Canned, slowly varying numbers for --demo screenshots.</summary>
public sealed class DemoSystemSource : ISystemSource
{
    private long tick;
    public CpuTimes Cpu() { tick++; return new(tick * 60, tick * 100, tick * 4); }   // ~ 40% busy
    public (long Used, long Total) Memory() => (10_400_000_000, 16_900_000_000);
    public (long Received, long Sent) Network() => (tick * 1_800_000, tick * 240_000);
    public (long Free, long Total, string Name)? SystemDrive() => (212_000_000_000, 476_000_000_000, "C:");
    public PowerReading Power() => new(72, true, false, true, TimeSpan.FromMinutes(165));
    public TimeSpan Uptime() => new(2, 4, 12, 0);
}

public enum BatteryAlert { None, Low20, Low10 }

/// <summary>Decides when to warn about a draining battery. First reading is a silent baseline; plugging in re-arms both levels.</summary>
public sealed class BatteryWatcher
{
    private readonly HashSet<int> alerted = [];
    private bool seen;
    private bool? onBattery;

    public bool PlugChanged { get; private set; }

    public BatteryAlert Update(PowerReading reading)
    {
        PlugChanged = seen && onBattery != reading.OnBattery;
        var first = !seen;
        seen = true;
        onBattery = reading.OnBattery;
        if (!reading.HasBattery || reading.Percent is not { } p)
            return BatteryAlert.None;
        if (!reading.OnBattery)
        {
            alerted.Clear();
            return BatteryAlert.None;
        }
        foreach (var level in new[] { 20, 10 })
            if (p > level)
                alerted.Remove(level);
        BatteryAlert result = BatteryAlert.None;
        foreach (var (level, alert) in new[] { (20, BatteryAlert.Low20), (10, BatteryAlert.Low10) })
        {
            if (p <= level && alerted.Add(level) && !first)
                result = alert;   // the lower level wins when one reading crosses both
        }
        return result;
    }
}
