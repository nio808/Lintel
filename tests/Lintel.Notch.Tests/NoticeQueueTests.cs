// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Core.Notch;
using Lintel.Notch.State;

namespace Lintel.Notch.Tests;

public sealed class NoticeQueueTests
{
    private readonly NoticeQueue q = new();

    [Fact]
    public void FirstInFirstOut()
    {
        Assert.True(q.Enqueue(N("a")));
        Assert.False(q.Enqueue(N("b")));
        Assert.False(q.Enqueue(N("c")));
        Assert.Equal("a", q.Current?.Id);
        Assert.True(q.Advance());
        Assert.Equal("b", q.Current?.Id);
        q.Advance();
        Assert.Equal("c", q.Current?.Id);
        Assert.True(q.Advance());
        Assert.Null(q.Current);
        Assert.False(q.Advance());
    }

    [Fact]
    public void CriticalPreemptsAndThePreemptedNoticeWaitsAtTheFront()
    {
        q.Enqueue(N("info"));
        q.Enqueue(N("next"));
        Assert.True(q.Enqueue(N("alarm", NoticeTone.Critical)));
        Assert.Equal("alarm", q.Current?.Id);
        Assert.Equal(["info", "next"], q.Waiting.Select(n => n.Id));
    }

    [Fact]
    public void CriticalJumpsTheQueueBehindOtherCriticals()
    {
        q.Enqueue(N("alarm1", NoticeTone.Critical));
        q.Enqueue(N("info"));
        q.Enqueue(N("alarm2", NoticeTone.Critical));
        Assert.Equal("alarm1", q.Current?.Id);
        Assert.Equal(["alarm2", "info"], q.Waiting.Select(n => n.Id));
    }

    [Fact]
    public void SameIdReplacesShowingOrWaiting()
    {
        q.Enqueue(N("a", title: "old"));
        q.Enqueue(N("b", title: "old"));
        Assert.True(q.Enqueue(N("a", title: "new")));   // showing → host restarts its timer
        Assert.Equal("new", q.Current?.Title);
        Assert.False(q.Enqueue(N("b", title: "new")));
        Assert.Single(q.Waiting);
        Assert.Equal("new", q.Waiting[0].Title);
    }

    [Fact]
    public void AtMostFiveWaitDroppingTheOldestNonCritical()
    {
        q.Enqueue(N("showing"));
        q.Enqueue(N("c1", NoticeTone.Critical));   // pre-empts; "showing" waits first
        for (var i = 0; i < 6; i++)
            q.Enqueue(N($"n{i}"));
        Assert.Equal(NoticeQueue.MaxWaiting, q.Waiting.Count);
        Assert.Equal(["n2", "n3", "n4", "n5"], q.Waiting.Select(n => n.Id).Skip(1));
        Assert.DoesNotContain(q.Waiting, n => n.Id == "showing");
    }

    [Fact]
    public void DismissRemovesShowingOrWaiting()
    {
        q.Enqueue(N("a"));
        q.Enqueue(N("b"));
        q.Enqueue(N("c"));
        Assert.False(q.Dismiss("c"));
        Assert.True(q.Dismiss("a"));
        Assert.Equal("b", q.Current?.Id);
        Assert.Empty(q.Waiting);
    }

    private static NotchNotice N(string id, NoticeTone tone = NoticeTone.Info, string title = "t") => new() { Id = id, Title = title, Tone = tone };
}
