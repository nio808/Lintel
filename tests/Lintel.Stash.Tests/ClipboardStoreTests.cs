// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Stash.Tests;

public sealed class ClipboardStoreTests
{
    private readonly ManualTime time = new();

    private ClipboardStore Store(ClipboardLimits? limits = null) => new(time, limits);

    private ClipEntry Text(string s) => ClipEntry.FromText(s, time.GetUtcNow());

    [Fact]
    public void DuplicateMovesToTopAndKeepsPin()
    {
        var store = Store();
        store.Add(Text("a"));
        var b = store.Add(Text("b"))!;
        store.Pin(b.Id, true);
        time.Advance(TimeSpan.FromMinutes(1));
        store.Add(Text("a"));
        store.Add(Text("c"));
        time.Advance(TimeSpan.FromMinutes(1));
        store.Add(Text("b"));
        Assert.Equal(3, store.Count);
        Assert.Equal(["b", "c", "a"], store.Items.Select(e => e.Text));
        Assert.True(store.Items[0].Pinned);
    }

    [Fact]
    public void PinnedFirstThenNewest()
    {
        var store = Store();
        var a = store.Add(Text("a"))!;
        store.Add(Text("b"));
        store.Add(Text("c"));
        store.Pin(a.Id, true);
        Assert.Equal(["a", "c", "b"], store.Items.Select(e => e.Text));
    }

    [Fact]
    public void CountCapTrimsOldestUnpinnedOnly()
    {
        var store = Store(new ClipboardLimits { MaxEntries = 3 });
        var first = store.Add(Text("0"))!;
        store.Pin(first.Id, true);
        for (var i = 1; i <= 6; i++)
            store.Add(Text(i.ToString()));
        Assert.Equal(["0", "6", "5", "4"], store.Items.Select(e => e.Text));
    }

    [Fact]
    public void OversizeTextAndImagesAreRejected()
    {
        var store = Store();
        Assert.Null(store.Add(Text(new string('x', 100 * 1024 + 1))));
        Assert.NotNull(store.Add(Text(new string('x', 100 * 1024))));
        Assert.Null(store.Add(Text("   ")));
        Assert.Null(store.Add(ClipEntry.FromImage(new byte[4 * 1024 * 1024 + 1], 1, 1, time.GetUtcNow())));
        Assert.NotNull(store.Add(ClipEntry.FromImage(new byte[1000], 1, 1, time.GetUtcNow())));
    }

    [Fact]
    public void ImageCountIsCappedAtTwenty()
    {
        var store = Store();
        for (var i = 0; i < 25; i++)
            store.Add(ClipEntry.FromImage([(byte)i, 1, 2], 1, 1, time.GetUtcNow()));
        Assert.Equal(20, store.Items.Count(e => e.Kind == ClipKind.Image));
    }

    [Fact]
    public void ExpiryDropsOldUnpinnedOnly()
    {
        var store = Store();
        var old = store.Add(Text("old"))!;
        var pinned = store.Add(Text("pinned"))!;
        store.Pin(pinned.Id, true);
        time.Advance(TimeSpan.FromDays(29));
        store.Add(Text("recent"));
        time.Advance(TimeSpan.FromDays(2));
        Assert.True(store.Expire());
        Assert.DoesNotContain(store.Items, e => e.Id == old.Id);
        Assert.Contains(store.Items, e => e.Text == "pinned");
        Assert.Contains(store.Items, e => e.Text == "recent");
    }

    [Fact]
    public void SearchMatchesAllTermsCaseInsensitively()
    {
        var store = Store();
        store.Add(Text("Hello World"));
        store.Add(Text("hello there"));
        store.Add(ClipEntry.FromFiles([@"C:\docs\Report.pdf"], time.GetUtcNow()));
        Assert.Equal(2, store.Search("HELLO").Count);
        Assert.Single(store.Search("hello wor"));
        Assert.Single(store.Search("report"));
        Assert.Equal(3, store.Search("").Count);
        Assert.Empty(store.Search("nope"));
    }

    [Fact]
    public void RemoveAndClear()
    {
        var store = Store();
        var a = store.Add(Text("a"))!;
        var b = store.Add(Text("b"))!;
        store.Pin(b.Id, true);
        Assert.True(store.Remove(a.Id));
        store.Add(Text("c"));
        store.Clear(keepPinned: true);
        Assert.Equal(["b"], store.Items.Select(e => e.Text));
        store.Clear(keepPinned: false);
        Assert.Equal(0, store.Count);
    }
}
