// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Core.Notch;

namespace Lintel.Notch.State;

/// <summary>
/// FIFO, at most 5 waiting, Critical jumps the queue (and pre-empts a non-critical notice, which
/// waits at the front), the same Id replaces a waiting or showing notice. Pure; single-threaded.
/// </summary>
public sealed class NoticeQueue
{
    public const int MaxWaiting = 5;

    private readonly List<NotchNotice> waiting = [];

    public NotchNotice? Current { get; private set; }
    public IReadOnlyList<NotchNotice> Waiting => waiting;

    /// <summary>True when <see cref="Current"/> changed (or was replaced): the host restarts its display timer.</summary>
    public bool Enqueue(NotchNotice notice)
    {
        if (Current?.Id == notice.Id)
        {
            Current = notice;
            return true;
        }
        var index = waiting.FindIndex(n => n.Id == notice.Id);
        if (index >= 0)
        {
            waiting[index] = notice;
            return false;
        }
        if (Current is null)
        {
            Current = notice;
            return true;
        }
        if (notice.Tone == NoticeTone.Critical)
        {
            if (Current.Tone != NoticeTone.Critical)
            {
                waiting.Insert(0, Current);
                Current = notice;
                Trim();
                return true;
            }
            waiting.Insert(waiting.FindLastIndex(n => n.Tone == NoticeTone.Critical) + 1, notice);
        }
        else
        {
            waiting.Add(notice);
        }
        Trim();
        return false;
    }

    /// <summary>Current expired or was clicked: show the next one. True when Current changed.</summary>
    public bool Advance()
    {
        if (Current is null && waiting.Count == 0)
            return false;
        Current = waiting.Count > 0 ? waiting[0] : null;
        if (waiting.Count > 0)
            waiting.RemoveAt(0);
        return true;
    }

    /// <summary>True when the dismissed notice was the current one (Current changed).</summary>
    public bool Dismiss(string id)
    {
        if (Current?.Id == id)
            return Advance();
        waiting.RemoveAll(n => n.Id == id);
        return false;
    }

    public void Clear()
    {
        Current = null;
        waiting.Clear();
    }

    private void Trim()
    {
        while (waiting.Count > MaxWaiting)
        {
            var oldest = waiting.FindIndex(n => n.Tone != NoticeTone.Critical);
            waiting.RemoveAt(oldest >= 0 ? oldest : 0);
        }
    }
}
