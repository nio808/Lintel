// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows.Threading;

namespace Lintel.Stash;

/// <summary>
/// FileSystemWatcher on the top level of the Downloads folder feeding a <see cref="DownloadTracker"/>.
/// A 1 s poll (size + existence) runs only while a download is active or undecided.
/// </summary>
internal sealed class DownloadsWatcher : IDisposable
{
    private readonly string folder;
    private readonly DownloadTracker tracker;
    private readonly Dispatcher ui;
    private readonly TimeProvider time;
    private readonly Action<FinishedDownload> finished;
    private readonly DispatcherTimer poll;
    private FileSystemWatcher? watcher;

    public DownloadsWatcher(string folder, DownloadTracker tracker, Dispatcher ui, TimeProvider time, Action<FinishedDownload> finished)
    {
        this.folder = folder;
        this.tracker = tracker;
        this.ui = ui;
        this.time = time;
        this.finished = finished;
        poll = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
        poll.Tick += (_, _) => Poll();
    }

    public bool Start()
    {
        if (!Directory.Exists(folder))
            return false;
        watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            InternalBufferSize = 32 * 1024,
        };
        watcher.Created += (_, e) => ui.BeginInvoke(() => OnSeen(e.FullPath));
        watcher.Changed += (_, e) => ui.BeginInvoke(() => OnChanged(e.FullPath));
        watcher.Deleted += (_, e) => ui.BeginInvoke(() => OnDeleted(e.FullPath));
        watcher.Renamed += (_, e) => ui.BeginInvoke(() => OnRenamed(e.OldFullPath, e.FullPath));
        watcher.EnableRaisingEvents = true;
        return true;
    }

    private DateTimeOffset Now => time.GetUtcNow();

    private void OnSeen(string path)
    {
        if (DownloadTracker.IsTempFile(path))
            tracker.TempSeen(path, SizeOf(path), Now);
        else
            Report(tracker.FileAppeared(path, SizeOf(path), Now));
        UpdatePoll();
    }

    private void OnChanged(string path)
    {
        if (DownloadTracker.IsTempFile(path))
            tracker.TempSeen(path, SizeOf(path), Now);
        UpdatePoll();
    }

    private void OnDeleted(string path)
    {
        if (DownloadTracker.IsTempFile(path))
            Report(tracker.TempGone(path, Now));
        UpdatePoll();
    }

    private void OnRenamed(string oldPath, string newPath)
    {
        var oldTemp = DownloadTracker.IsTempFile(oldPath);
        var newTemp = DownloadTracker.IsTempFile(newPath);
        if (oldTemp && newTemp)
            tracker.TempRenamed(oldPath, newPath, SizeOf(newPath), Now);
        else if (oldTemp)
        {
            Report(tracker.TempGone(oldPath, Now));
            Report(tracker.FileAppeared(newPath, SizeOf(newPath), Now));
        }
        else if (newTemp)
            tracker.TempSeen(newPath, SizeOf(newPath), Now);
        else
            Report(tracker.FileAppeared(newPath, SizeOf(newPath), Now));
        UpdatePoll();
    }

    private void Poll()
    {
        var now = Now;
        foreach (var path in tracker.PollPaths)
        {
            if (File.Exists(path))
                tracker.TempSeen(path, SizeOf(path), now);
            else
                Report(tracker.TempGone(path, now));
        }
        Report(tracker.Tick(now));
        UpdatePoll();
    }

    private void Report(IReadOnlyList<FinishedDownload> done)
    {
        foreach (var d in done)
            finished(d);
    }

    private void UpdatePoll()
    {
        if (tracker.NeedsPolling && !poll.IsEnabled)
            poll.Start();
        else if (!tracker.NeedsPolling && poll.IsEnabled)
            poll.Stop();
    }

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    public void Dispose()
    {
        poll.Stop();
        if (watcher is { } w)
        {
            watcher = null;
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
    }
}
