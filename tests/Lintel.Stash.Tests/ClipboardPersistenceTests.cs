// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text;

namespace Lintel.Stash.Tests;

public sealed class ClipboardPersistenceTests
{
    [Fact]
    public void RoundTripKeepsTextFilesAndPinsButNotImages()
    {
        using var dir = new TempDir();
        var path = dir.File("history.bin");
        var now = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var pinned = ClipEntry.FromText("some text 🙂", now);
        pinned.Pinned = true;
        ClipboardPersistence.Save(path, [pinned, ClipEntry.FromFiles([@"C:\a.txt", @"C:\b.txt"], now), ClipEntry.FromImage([1, 2, 3], 1, 1, now)]);

        var loaded = ClipboardPersistence.Load(path);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("some text 🙂", loaded[0].Text);
        Assert.True(loaded[0].Pinned);
        Assert.Equal(now, loaded[0].Created);
        Assert.Equal([@"C:\a.txt", @"C:\b.txt"], loaded[1].Files);
    }

    [Fact]
    public void FileOnDiskIsEncrypted()
    {
        using var dir = new TempDir();
        var path = dir.File("history.bin");
        ClipboardPersistence.Save(path, [ClipEntry.FromText("hunter2-plaintext-marker", DateTimeOffset.UtcNow)]);
        var bytes = File.ReadAllBytes(path);
        Assert.DoesNotContain("hunter2", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("hunter2", Encoding.Unicode.GetString(bytes));
    }

    [Fact]
    public void CorruptFileIsMovedAsideAndGivesEmpty()
    {
        using var dir = new TempDir();
        var path = dir.File("history.bin");
        File.WriteAllText(path, "this is not a DPAPI blob");
        Assert.Empty(ClipboardPersistence.Load(path));
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void MissingFileGivesEmpty()
    {
        using var dir = new TempDir();
        Assert.Empty(ClipboardPersistence.Load(dir.File("none.bin")));
    }

    [Fact]
    public void LoadedEntriesMergeIntoTheStore()
    {
        var time = new ManualTime();
        var store = new ClipboardStore(time);
        var old = ClipEntry.FromText("old", time.GetUtcNow() - TimeSpan.FromDays(2));
        store.Add(ClipEntry.FromText("new", time.GetUtcNow()));
        store.Load([old, ClipEntry.FromText("new", time.GetUtcNow() - TimeSpan.FromDays(1))]);
        Assert.Equal(["new", "old"], store.Items.Select(e => e.Text));
    }
}
