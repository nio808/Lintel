// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Stash.Tests;

public sealed class DownloadTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Temp = @"C:\Dl\movie.mkv.crdownload";
    private const string Final = @"C:\Dl\movie.mkv";

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    [Theory]
    [InlineData(@"C:\a\x.CRDOWNLOAD", true)]
    [InlineData(@"x.part", true)]
    [InlineData(@"x.partial", true)]
    [InlineData(@"x.download", true)]
    [InlineData(@"x.opdownload", true)]
    [InlineData(@"x.zip", false)]
    [InlineData(@"crdownload", false)]
    public void RecognisesTempExtensions(string path, bool expected) => Assert.Equal(expected, DownloadTracker.IsTempFile(path));

    [Fact]
    public void DisplayNameStripsTheTempExtension() => Assert.Equal("movie.mkv", DownloadTracker.DisplayName(Temp));

    [Fact]
    public void StartsOnFirstSightAndMeasuresSpeedOverAMovingWindow()
    {
        var t = new DownloadTracker();
        t.TempSeen(Temp, 0, At(0));
        Assert.Single(t.Active);
        Assert.Equal(0, t.Active[0].BytesPerSecond);
        t.TempSeen(Temp, 1000, At(1));
        t.TempSeen(Temp, 3000, At(2));
        Assert.Equal(1500, t.Active[0].BytesPerSecond, 1);       // 3000 B over 2 s
        Assert.Equal(3000, t.Active[0].Bytes);
        t.TempSeen(Temp, 3000, At(7.5));                          // samples older than 5 s fall out; stalled
        t.TempSeen(Temp, 3000, At(8.5));
        Assert.Equal(0, t.Active[0].BytesPerSecond, 1);
    }

    [Fact]
    public void RenameToFinalNameCompletes()
    {
        var t = new DownloadTracker();
        t.TempSeen(Temp, 5_000, At(0));
        t.TempSeen(Temp, 9_000, At(1));
        Assert.Empty(t.TempGone(Temp, At(2)));                    // undecided until a file shows up
        var done = t.FileAppeared(Final, 9_500, At(2.1));
        var fin = Assert.Single(done);
        Assert.Equal(Final, fin.Path);
        Assert.Equal("movie.mkv", fin.Name);
        Assert.Equal(9_500, fin.Bytes);
        Assert.Empty(t.Active);
        Assert.Single(t.History);
    }

    [Fact]
    public void FileAppearingBeforeTheTempDisappearsStillCompletes()
    {
        var t = new DownloadTracker();
        t.TempSeen(Temp, 100, At(0));
        Assert.Empty(t.FileAppeared(Final, 100, At(1)));
        Assert.Single(t.TempGone(Temp, At(1.5)));
    }

    [Fact]
    public void DifferentFinalNameStillMatchesByTime()
    {
        var t = new DownloadTracker();
        t.TempSeen(@"C:\Dl\Unconfirmed 123.crdownload", 100, At(0));
        t.TempGone(@"C:\Dl\Unconfirmed 123.crdownload", At(1));
        var done = t.FileAppeared(@"C:\Dl\report.pdf", 100, At(1.2));
        Assert.Equal("report.pdf", Assert.Single(done).Name);
    }

    [Fact]
    public void DeletedTempWithNoNewFileIsACancel()
    {
        var t = new DownloadTracker();
        t.TempSeen(Temp, 100, At(0));
        t.TempGone(Temp, At(1));
        Assert.Empty(t.Tick(At(3)));
        Assert.True(t.NeedsPolling);                              // still undecided inside the 3 s window
        Assert.Empty(t.Tick(At(4.5)));
        Assert.Empty(t.Active);
        Assert.False(t.NeedsPolling);
        Assert.Empty(t.History);
    }

    [Fact]
    public void FileMoreThanThreeSecondsAwayIsNotACompletion()
    {
        var t = new DownloadTracker();
        t.TempSeen(Temp, 100, At(0));
        t.TempGone(Temp, At(1));
        t.Tick(At(4.5));
        Assert.Empty(t.FileAppeared(@"C:\Dl\unrelated.txt", 5, At(6)));
        Assert.Empty(t.History);
    }

    [Fact]
    public void TempRenamedToAnotherTempKeepsTheSameDownload()
    {
        var t = new DownloadTracker();
        t.TempSeen(@"C:\Dl\Unconfirmed 1.crdownload", 100, At(0));
        t.TempRenamed(@"C:\Dl\Unconfirmed 1.crdownload", Temp, 400, At(1));
        var a = Assert.Single(t.Active);
        Assert.Equal("movie.mkv", a.Name);
        Assert.Equal(At(0), a.Started);
    }

    [Fact]
    public void ReappearingTempCancelsThePendingDisappearance()
    {
        var t = new DownloadTracker();
        t.TempSeen(Temp, 100, At(0));
        t.TempGone(Temp, At(1));
        Assert.Empty(t.Active);
        t.TempSeen(Temp, 200, At(1.5));                           // resumed
        Assert.Single(t.Active);
        Assert.Empty(t.Tick(At(10)));
        Assert.Single(t.Active);
    }

    [Fact]
    public void HistoryKeepsTheLastTenNewestFirst()
    {
        var t = new DownloadTracker();
        for (var i = 0; i < 12; i++)
        {
            var temp = $@"C:\Dl\f{i}.zip.part";
            t.TempSeen(temp, 10, At(i * 10));
            t.TempGone(temp, At(i * 10 + 1));
            t.FileAppeared($@"C:\Dl\f{i}.zip", 10, At(i * 10 + 1));
        }
        Assert.Equal(10, t.History.Count);
        Assert.Equal("f11.zip", t.History[0].Name);
        Assert.Equal("f2.zip", t.History[^1].Name);
    }

    [Fact]
    public void NonTempFilesNeverStartADownload()
    {
        var t = new DownloadTracker();
        Assert.Empty(t.FileAppeared(Final, 1, At(0)));
        Assert.Empty(t.Active);
    }
}
