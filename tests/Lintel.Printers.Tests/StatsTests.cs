// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Lintel.Core.Settings;

namespace Lintel.Printers.Tests;

public sealed class StatsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "Lintel.Printers.Tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    public StatsTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private string SettingsFile => Path.Combine(dir, "settings.json");

    [Fact]
    public void StatisticsSurviveARestart()
    {
        using (var settings = new JsonSettingsStore(SettingsFile))
        {
            var store = new StatsStore(settings);
            store.RecordJob("EPSON", 12, Now, TimeSpan.FromMinutes(2));
            store.RecordMaintenance("EPSON", MaintenanceAction.HeadClean, Now.AddHours(1));
            store.RecordMaintenance("EPSON", MaintenanceAction.NozzleCheck, Now.AddHours(1));
            store.RecordPadReset("EPSON", 88, Now.AddDays(1));
            settings.Flush();
        }
        using var again = new JsonSettingsStore(SettingsFile);
        var s = new StatsStore(again).Get("epson");   // names are case-insensitive
        Assert.Equal(12, s.LifetimePages);
        Assert.Equal(0, s.PagesSinceHeadClean);
        Assert.Equal(1, s.HeadCleans);
        Assert.Equal(1, s.NozzleChecks);
        Assert.Equal(Now.AddHours(1), s.LastHeadClean);
        Assert.Equal(Now, s.LastPrint);
        Assert.Equal(6, s.LearnedPagesPerMinute);
        Assert.Equal(88, s.PadPercentBeforeReset);
        Assert.Equal(Now.AddDays(1), s.LastPadReset);
        Assert.Single(s.RecentHeadCleans);
    }

    [Fact]
    public void PagesAccumulateSinceTheLastClean()
    {
        var store = new StatsStore(null);
        store.RecordJob("P", 10, Now);
        store.RecordMaintenance("P", MaintenanceAction.HeadClean, Now);
        store.RecordJob("P", 7, Now);
        var s = store.Get("P");
        Assert.Equal(17, s.LifetimePages);
        Assert.Equal(7, s.PagesSinceHeadClean);
        Assert.Equal(17, s.PagesSincePowerClean);
    }

    [Fact]
    public void PowerCleanRestartsBothCountersAndTheHeadCleanStreak()
    {
        var store = new StatsStore(null);
        store.RecordJob("P", 10, Now);
        for (var i = 0; i < 3; i++)
            store.RecordMaintenance("P", MaintenanceAction.HeadClean, Now.AddHours(i));
        store.RecordMaintenance("P", MaintenanceAction.PowerClean, Now.AddHours(4));
        var s = store.Get("P");
        Assert.Equal(0, s.PagesSinceHeadClean);
        Assert.Equal(0, s.PagesSincePowerClean);
        Assert.Empty(s.RecentHeadCleans);
        Assert.Equal(3, s.HeadCleans);
        Assert.Equal(1, s.PowerCleans);
        Assert.DoesNotContain(MaintenanceAction.PowerClean, RecommendationEngine.Evaluate(s, new Thresholds(), Now.AddHours(14), null).Highlight);
    }

    [Fact]
    public void ZeroPageJobStillUpdatesLastPrintAndResetsErase()
    {
        var store = new StatsStore(null);
        store.RecordJob("P", 0, Now);
        Assert.Equal(Now, store.Get("P").LastPrint);
        Assert.Equal(0, store.Get("P").LifetimePages);
        store.RecordJob("P", 3, Now);
        store.Reset("P");
        Assert.Equal(0, store.Get("P").LifetimePages);
        store.RecordJob("Q", 3, Now);
        store.ResetAll();
        Assert.Equal(0, store.Get("Q").LifetimePages);
    }

    [Fact]
    public void KeepsOnlyTheLastTenHeadCleans()
    {
        var store = new StatsStore(null);
        for (var i = 0; i < 12; i++)
            store.RecordMaintenance("P", MaintenanceAction.HeadClean, Now.AddMinutes(i));
        Assert.Equal(10, store.Get("P").RecentHeadCleans.Count);
        Assert.Equal(12, store.Get("P").HeadCleans);
    }
}
