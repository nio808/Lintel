// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;

namespace Lintel.Stash.Tests;

public sealed class ScratchpadStoreTests
{
    private readonly ManualTime time = new();

    [Fact]
    public void AutosavesEightHundredMsAfterTheLastKeystroke()
    {
        using var dir = new TempDir();
        using var store = new ScratchpadStore(dir.Path, time);
        store.Load();
        var id = store.Tabs[0].Id;
        var file = dir.File(id + ".txt");

        store.SetText(id, "h");
        time.Advance(TimeSpan.FromMilliseconds(500));
        store.SetText(id, "hi");                         // restarts the 800 ms window
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False(File.Exists(file));
        time.Advance(TimeSpan.FromMilliseconds(301));
        Assert.Equal("hi", File.ReadAllText(file));
    }

    [Fact]
    public void FlushWritesImmediatelyAndReloadsFromDisk()
    {
        using var dir = new TempDir();
        string id;
        using (var store = new ScratchpadStore(dir.Path, time))
        {
            store.Load();
            id = store.Tabs[0].Id;
            store.SetText(id, "unicode ✓\nline 2");
            var second = store.AddTab("second")!;
            store.Rename(second.Id, "Todo");
            store.Flush();
        }
        using var again = new ScratchpadStore(dir.Path, time);
        again.Load();
        Assert.Equal(["Note 1", "Todo"], again.Tabs.Select(t => t.Title));
        Assert.Equal("unicode ✓\nline 2", again.GetText(id));
        Assert.Equal("second", again.GetText(again.Tabs[1].Id));
    }

    [Fact]
    public void AtMostEightTabsAndLowestFreeNumberIsUsed()
    {
        using var store = new ScratchpadStore(null, time);
        store.Load();
        for (var i = 0; i < 7; i++)
            Assert.NotNull(store.AddTab());
        Assert.Null(store.AddTab());
        Assert.Equal(8, store.Tabs.Count);
        store.RemoveTab(store.Tabs[2].Id);                // frees "Note 3"
        Assert.Equal("Note 3", store.AddTab()!.Title);
    }

    [Fact]
    public void RemovingATabSelectsItsNeighbourAndDeletesTheFile()
    {
        using var dir = new TempDir();
        using var store = new ScratchpadStore(dir.Path, time);
        store.Load();
        var a = store.Tabs[0];
        var b = store.AddTab("b")!;
        var c = store.AddTab("c")!;
        store.Flush();
        Assert.True(File.Exists(dir.File(b.Id + ".txt")));
        Assert.Equal(c.Id, store.RemoveTab(b.Id));       // neighbour to the right
        Assert.False(File.Exists(dir.File(b.Id + ".txt")));
        Assert.Equal(a.Id, store.RemoveTab(c.Id));       // last tab removed: neighbour to the left
        var fresh = store.RemoveTab(a.Id);               // last remaining: a fresh tab appears
        Assert.Single(store.Tabs);
        Assert.Equal(store.Tabs[0].Id, fresh);
        Assert.NotEqual(a.Id, fresh);
    }

    [Fact]
    public void RenameTrimsAndIgnoresBlank()
    {
        using var store = new ScratchpadStore(null, time);
        store.Load();
        var id = store.Tabs[0].Id;
        store.Rename(id, "   ");
        Assert.Equal("Note 1", store.Tabs[0].Title);
        store.Rename(id, "  Plans ");
        Assert.Equal("Plans", store.Tabs[0].Title);
    }

    [Fact]
    public void UnsafeIdsInTabsJsonAreDropped()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("tabs.json"), "[{\"Id\":\"..\\\\evil\",\"Title\":\"x\"},{\"Id\":\"ok1\",\"Title\":\"Fine\"}]");
        using var store = new ScratchpadStore(dir.Path, time);
        store.Load();
        Assert.Equal(["ok1"], store.Tabs.Select(t => t.Id));
    }

    [Fact]
    public void DisposeFlushesPendingText()
    {
        using var dir = new TempDir();
        var store = new ScratchpadStore(dir.Path, time);
        store.Load();
        var id = store.Tabs[0].Id;
        store.SetText(id, "pending");
        store.Dispose();
        Assert.Equal("pending", File.ReadAllText(dir.File(id + ".txt")));
    }
}
