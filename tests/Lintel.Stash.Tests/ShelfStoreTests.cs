// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;

namespace Lintel.Stash.Tests;

public sealed class ShelfStoreTests
{
    private readonly ManualTime time = new();

    [Fact]
    public void CapsAtFiftyDroppingOldest()
    {
        var store = new ShelfStore(null, time, _ => true);
        for (var i = 0; i < 60; i++)
            store.Add(ShelfKind.File, $@"C:\f\{i}.txt");
        Assert.Equal(50, store.Items.Count);
        Assert.EndsWith(@"59.txt", store.Items[0].Value);
        Assert.EndsWith(@"10.txt", store.Items[^1].Value);
    }

    [Fact]
    public void DuplicatesMoveToFront()
    {
        var store = new ShelfStore(null, time, _ => true);
        store.Add(ShelfKind.File, @"C:\a.txt");
        store.Add(ShelfKind.File, @"C:\b.txt");
        store.Add(ShelfKind.File, @"c:\A.TXT");
        Assert.Equal(2, store.Items.Count);
        Assert.Equal(@"c:\A.TXT", store.Items[0].Value);
    }

    [Fact]
    public void MissingFilesAreFlaggedWithoutBeingRemoved()
    {
        using var dir = new TempDir();
        var real = dir.File("real.txt");
        File.WriteAllText(real, "x");
        var store = new ShelfStore(null, time);
        store.Add(ShelfKind.File, real);
        store.Add(ShelfKind.File, dir.File("gone.txt"));
        store.Add(ShelfKind.Text, "just text");
        Assert.False(store.IsMissing(store.Items[2]));
        Assert.True(store.IsMissing(store.Items[1]));
        Assert.False(store.IsMissing(store.Items[0]));
        Assert.Equal(3, store.Items.Count);
        File.Delete(real);
        Assert.True(store.IsMissing(store.Items[2]));
    }

    [Fact]
    public void PersistenceRoundTrip()
    {
        using var dir = new TempDir();
        var path = dir.File("sub/items.json");
        var store = new ShelfStore(path, time, _ => true);
        store.Add(ShelfKind.File, @"C:\Docs\Report.pdf");
        store.Add(ShelfKind.File, @"C:\Docs", isFolder: true);
        store.Add(ShelfKind.Link, "https://example.com/a?b=1");
        store.Add(ShelfKind.Text, "line one\nline two");

        var again = new ShelfStore(path, time, _ => true);
        again.Load();
        Assert.Equal(store.Items, again.Items);
        Assert.Equal(["line one line two", "example.com/a", "Docs", "Report.pdf"], again.Items.Select(i => i.Name));
        Assert.True(again.Items[2].IsFolder);
    }

    [Fact]
    public void RemoveClearAndCorruptFile()
    {
        using var dir = new TempDir();
        var path = dir.File("items.json");
        var store = new ShelfStore(path, time, _ => true);
        var a = store.Add(ShelfKind.Text, "a")!;
        store.Add(ShelfKind.Text, "b");
        store.Remove(a.Id);
        Assert.Single(store.Items);
        store.Clear();
        Assert.Empty(store.Items);

        File.WriteAllText(path, "{ not json");
        var broken = new ShelfStore(path, time, _ => true);
        broken.Load();
        Assert.Empty(broken.Items);
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void EmptyValuesAreIgnored()
    {
        var store = new ShelfStore(null, time, _ => true);
        Assert.Null(store.Add(ShelfKind.Text, "  "));
        Assert.Empty(store.Items);
    }
}
