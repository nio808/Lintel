// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;

namespace Lintel.Tools.KeepAwake;

public enum KeepAwakeKind { Timed, Indefinite, WhileApp }
public enum KeepAwakeEnd { Expired, LowBattery, AppExited }

public readonly record struct PowerReading(int? Percent, bool OnBattery, bool Charging = false, bool HasBattery = true, TimeSpan? Remaining = null);

/// <summary>One keep-awake request and the rules that end it. Pure: the owner supplies time, battery and process state.</summary>
public sealed class KeepAwakeSession
{
    public const int LowBatteryPercent = 20;
    private readonly DateTimeOffset? until;

    private KeepAwakeSession(KeepAwakeKind kind, DateTimeOffset? until, string? app, bool keepDisplay, bool autoOff)
    {
        Kind = kind;
        this.until = until;
        AppName = app;
        KeepDisplay = keepDisplay;
        AutoOffOnLowBattery = autoOff;
    }

    public KeepAwakeKind Kind { get; }
    public string? AppName { get; }
    public bool KeepDisplay { get; }
    public bool AutoOffOnLowBattery { get; }

    public static KeepAwakeSession Timed(DateTimeOffset now, TimeSpan duration, bool keepDisplay, bool autoOff) =>
        new(KeepAwakeKind.Timed, now + duration, null, keepDisplay, autoOff);

    public static KeepAwakeSession Indefinite(bool keepDisplay, bool autoOff) => new(KeepAwakeKind.Indefinite, null, null, keepDisplay, autoOff);

    public static KeepAwakeSession WhileApp(string processName, bool keepDisplay, bool autoOff) =>
        new(KeepAwakeKind.WhileApp, null, processName, keepDisplay, autoOff);

    /// <summary>Same session (same end) with different options.</summary>
    public KeepAwakeSession WithOptions(bool keepDisplay, bool autoOff) => new(Kind, until, AppName, keepDisplay, autoOff);

    public TimeSpan? Remaining(DateTimeOffset now) => until is { } u ? (u > now ? u - now : TimeSpan.Zero) : null;

    /// <summary>Null while the session should go on; otherwise why it ends.</summary>
    public KeepAwakeEnd? Check(DateTimeOffset now, PowerReading power, bool appRunning)
    {
        if (AutoOffOnLowBattery && power is { HasBattery: true, OnBattery: true, Percent: { } p } && p < LowBatteryPercent)
            return KeepAwakeEnd.LowBattery;
        if (until is { } u && now >= u)
            return KeepAwakeEnd.Expired;
        if (Kind == KeepAwakeKind.WhileApp && !appRunning)
            return KeepAwakeEnd.AppExited;
        return null;
    }
}

/// <summary>Sets or clears the system "stay awake" request.</summary>
public interface IAwakeBackend : IDisposable
{
    void Set(bool keepDisplay);
    void Clear();
}

/// <summary>SetThreadExecutionState is per thread, so one long-lived thread owns it and clears it on Clear/Dispose.</summary>
public sealed class ExecutionStateThread : IAwakeBackend
{
    private readonly BlockingCollection<bool?> commands = [];   // true = display+system, false = system only, null = clear
    private Thread? thread;

    public void Set(bool keepDisplay) => Send(keepDisplay);
    public void Clear() => Send(null);

    private void Send(bool? command)
    {
        if (commands.IsAddingCompleted)
            return;
        if (thread is null)
        {
            if (command is null)
                return;
            thread = new Thread(Run) { IsBackground = true, Name = "KeepAwake" };
            thread.Start();
        }
        commands.Add(command);
    }

    private void Run()
    {
        foreach (var command in commands.GetConsumingEnumerable())
        {
            var flags = Native.ES_CONTINUOUS;
            if (command is { } display)
                flags |= Native.ES_SYSTEM_REQUIRED | (display ? Native.ES_DISPLAY_REQUIRED : 0);
            Native.SetThreadExecutionState(flags);
        }
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
    }

    public void Dispose()
    {
        if (commands.IsAddingCompleted)
            return;
        if (thread is not null)
            commands.Add(null);
        commands.CompleteAdding();
        thread?.Join(1000);
    }
}
