// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Core.Services;

/// <summary>
/// Mutex Local\Lintel.SingleInstance + auto-reset events Local\Lintel.Show / .Quit.
/// The first instance listens; events are posted to the SynchronizationContext captured at construction (the UI thread when built there).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Lintel.SingleInstance";
    private const string ShowName = @"Local\Lintel.Show";
    private const string QuitName = @"Local\Lintel.Quit";

    private readonly Mutex mutex;
    private readonly EventWaitHandle show;
    private readonly EventWaitHandle quit;
    private readonly RegisteredWaitHandle? showWait;
    private readonly RegisteredWaitHandle? quitWait;
    private readonly SynchronizationContext? context = SynchronizationContext.Current;

    public SingleInstance()
    {
        // existence of the named mutex is the signal (never owned, so it cannot be abandoned).
        mutex = new Mutex(false, MutexName, out var created);
        IsFirst = created;
        show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName);
        quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitName);
        if (!IsFirst)
            return;
        showWait = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => Raise(ShowRequested), null, Timeout.Infinite, false);
        quitWait = ThreadPool.RegisterWaitForSingleObject(quit, (_, _) => Raise(QuitRequested), null, Timeout.Infinite, false);
    }

    public bool IsFirst { get; }

    public event Action? ShowRequested;

    public event Action? QuitRequested;

    /// <summary>Asks the first instance to show itself (what a second launch does).</summary>
    public void SignalShow() => show.Set();

    /// <summary>Asks a running instance to exit; no-op when none runs.</summary>
    public static void SignalQuit()
    {
        if (EventWaitHandle.TryOpenExisting(QuitName, out var handle))
        {
            using (handle)
                handle.Set();
        }
    }

    public void Dispose()
    {
        showWait?.Unregister(null);
        quitWait?.Unregister(null);
        show.Dispose();
        quit.Dispose();
        mutex.Dispose();
    }

    private void Raise(Action? handler)
    {
        if (handler is null)
            return;
        if (context is null)
            handler();
        else
            context.Post(_ => handler(), null);
    }
}
