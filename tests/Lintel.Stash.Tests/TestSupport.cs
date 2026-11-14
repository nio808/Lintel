// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;

namespace Lintel.Stash.Tests;

/// <summary>TimeProvider whose clock and timers only move when the test says so.</summary>
public sealed class ManualTime : TimeProvider
{
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new ManualTimer(this, callback, state);
        t.Change(dueTime, period);
        timers.Add(t);
        return t;
    }

    public void Advance(TimeSpan span)
    {
        var target = now + span;
        while (timers.Where(t => t.Due is not null && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault() is { } next)
        {
            now = next.Due!.Value;
            next.Fire();
        }
        now = target;
    }

    private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
            return true;
        }

        public void Fire()
        {
            Due = null;
            callback(state);
        }

        public void Dispose() => Due = null;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Lintel.Stash.Tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}
