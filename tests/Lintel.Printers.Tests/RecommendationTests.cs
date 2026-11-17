// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Printers.Tests;

public sealed class RecommendationTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Thresholds T = new();

    private static PrinterStats Fresh() => new() { LastPrint = Now.AddDays(-1) };

    [Fact]
    public void NothingToDo()
    {
        var r = RecommendationEngine.Evaluate(Fresh(), T, Now);
        Assert.Equal(Severity.Info, r.Severity);
        Assert.Empty(r.Highlight);
    }

    [Fact]
    public void IdleForTwoWeeksRecommendsNozzleCheckAndHeadClean()
    {
        var s = new PrinterStats { LastPrint = Now.AddDays(-18) };
        var r = RecommendationEngine.Evaluate(s, T, Now);
        Assert.Equal([MaintenanceAction.NozzleCheck, MaintenanceAction.HeadClean], r.Highlight);
        Assert.Contains("18 days", r.Text);
        Assert.Contains("limit 14", r.Text);
        Assert.Empty(RecommendationEngine.Evaluate(new PrinterStats { LastPrint = Now.AddDays(-13) }, T, Now).Highlight);
    }

    [Fact]
    public void IdleThresholdIsConfigurable() =>
        Assert.NotEmpty(RecommendationEngine.Evaluate(new PrinterStats { LastPrint = Now.AddDays(-8) }, T with { IdleDays = 7 }, Now).Highlight);

    [Fact]
    public void ARecentCleanRestartsTheIdleClock()
    {
        var s = new PrinterStats { LastPrint = Now.AddDays(-30), LastHeadClean = Now.AddDays(-2) };
        Assert.Empty(RecommendationEngine.Evaluate(s, T, Now).Highlight);
    }

    [Fact]
    public void PagesSinceLastCleanReasonShowsNumbers()
    {
        var s = Fresh();
        s.PagesSinceHeadClean = 512;
        var r = RecommendationEngine.Evaluate(s, T, Now);
        Assert.Equal("512 pages since last clean · limit 500 · check the nozzles, then head clean if needed", r.Text);
        Assert.Contains(MaintenanceAction.HeadClean, r.Highlight);
        s.PagesSinceHeadClean = 499;
        Assert.Empty(RecommendationEngine.Evaluate(s, T, Now).Highlight);
        Assert.NotEmpty(RecommendationEngine.Evaluate(s, T with { PagesBetweenCleans = 400 }, Now).Highlight);
    }

    [Fact]
    public void PowerCleanOnlyAfterThreeHeadCleansWithin24Hours()
    {
        var s = Fresh();
        s.RecentHeadCleans = [Now.AddHours(-20), Now.AddHours(-10)];
        Assert.DoesNotContain(MaintenanceAction.PowerClean, RecommendationEngine.Evaluate(s, T, Now).Highlight);

        s.RecentHeadCleans = [Now.AddHours(-30), Now.AddHours(-10), Now.AddHours(-1)];   // one is too old
        Assert.DoesNotContain(MaintenanceAction.PowerClean, RecommendationEngine.Evaluate(s, T, Now).Highlight);

        s.RecentHeadCleans = [Now.AddHours(-20), Now.AddHours(-10), Now.AddHours(-1)];
        var r = RecommendationEngine.Evaluate(s, T, Now);
        Assert.Equal([MaintenanceAction.PowerClean], r.Highlight);
        Assert.Contains("lot of ink", r.Text);
        Assert.Contains("ink pad", r.Text);
    }

    [Fact]
    public void NoSecondPowerCleanWithin12Hours()
    {
        var s = Fresh();
        s.RecentHeadCleans = [Now.AddHours(-20), Now.AddHours(-10), Now.AddHours(-1)];
        s.LastPowerClean = Now.AddHours(-11);
        Assert.Empty(RecommendationEngine.Evaluate(s, T, Now).Highlight);
        s.LastPowerClean = Now.AddHours(-13);
        Assert.Equal([MaintenanceAction.PowerClean], RecommendationEngine.Evaluate(s, T, Now).Highlight);
    }

    [Fact]
    public void NoPowerCleanAdviceWhenThePadIsAlmostFull()
    {
        var s = Fresh();
        s.RecentHeadCleans = [Now.AddHours(-20), Now.AddHours(-10), Now.AddHours(-1)];
        var r = RecommendationEngine.Evaluate(s, T, Now, padPercent: 96);
        Assert.Empty(r.Highlight);
        Assert.Equal(Severity.Critical, r.Severity);
    }

    [Theory]
    [InlineData(10, null)]
    [InlineData(79.9, null)]
    [InlineData(80, Severity.Warning)]
    [InlineData(94, Severity.Warning)]
    [InlineData(95, Severity.Critical)]
    [InlineData(120, Severity.Critical)]
    public void PadLevels(double percent, Severity? level) => Assert.Equal(level, RecommendationEngine.Pad(percent, T).Level);

    [Fact]
    public void PadTextsAndUnknown()
    {
        Assert.Contains("service soon", RecommendationEngine.Pad(97, T).Text);
        Assert.Contains("filling up", RecommendationEngine.Pad(85, T).Text);
        Assert.Null(RecommendationEngine.Pad(null, T).Level);
    }

    [Fact]
    public void ModelLimitsBeatTheReportedPercent()
    {
        var reading = new PadReading(50, null, 5700, null);
        Assert.Equal(50, PadMath.Worst(reading, null));
        Assert.Equal(95, PadMath.Worst(reading, new ModelLimits(6000, null, null)));
        Assert.Equal(90, PadMath.Worst(new PadReading(10, 60, 100, 5400), new ModelLimits(6000, 6000, null)));
    }

    [Fact]
    public void PadReadPolicy()
    {
        var p = new PadReadPolicy();
        var t = Now;
        Assert.True(p.ShouldRead("A", t, jobActive: false));            // first open
        Assert.False(p.ShouldRead("A", t, jobActive: true));            // never during a job
        p.Done("A", t);
        Assert.False(p.ShouldRead("A", t.AddMinutes(9), false));        // cached
        Assert.True(p.ShouldRead("A", t.AddMinutes(10), false));        // stale
        p.MarkDirty("A");
        Assert.True(p.ShouldRead("A", t.AddMinutes(1), false));         // after a job or a clean
        Assert.False(p.ShouldRead("A", t.AddMinutes(1), true));         // still not during a job
        p.Done("A", t.AddMinutes(1));
        Assert.False(p.ShouldRead("A", t.AddMinutes(2), false));
    }
}
