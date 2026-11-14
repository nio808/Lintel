// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Stash;

/// <summary>Runs the action once, <c>delay</c> after the last <see cref="Trigger"/> (on a timer thread). Uses TimeProvider so tests can drive it.</summary>
public sealed class Debouncer : IDisposable
{
    private readonly ITimer timer;
    private readonly TimeSpan delay;

    public Debouncer(TimeSpan delay, Action action, TimeProvider time)
    {
        this.delay = delay;
        timer = time.CreateTimer(_ => action(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Trigger() => timer.Change(delay, Timeout.InfiniteTimeSpan);

    public void Cancel() => timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    public void Dispose() => timer.Dispose();
}
