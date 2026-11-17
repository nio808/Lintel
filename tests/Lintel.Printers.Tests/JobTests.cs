// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Printers.Tests;

public sealed class JobTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Pr = "EPSON";

    private static SpoolerSnapshot Snap(params JobInfo[] jobs) => new([], jobs);
    private static JobInfo Job(int printed, int total, JobFlags flags = JobFlags.Printing) => new(1, Pr, "report.pdf", flags, printed, total, 1000);

    [Fact]
    public void EtaUsesObservedPagesPerMinuteOnceAPageCompleted()
    {
        var tracker = new JobTracker(_ => null);
        tracker.Update(Snap(Job(0, 10)), T0);
        Assert.Null(tracker.Active[0].Eta);                      // nothing observed, nothing learned: "estimating..."
        tracker.Update(Snap(Job(2, 10)), T0.AddSeconds(60));      // 2 pages / minute
        var eta = tracker.Active[0].Eta;
        Assert.NotNull(eta);
        Assert.InRange(eta!.Value.TotalSeconds, 238, 242);        // 8 pages left at 2 ppm
    }

    [Fact]
    public void EtaCountsDownBetweenPageChanges()
    {
        var tracker = new JobTracker(_ => null);
        tracker.Update(Snap(Job(0, 10)), T0);
        tracker.Update(Snap(Job(2, 10)), T0.AddSeconds(60));
        tracker.Update(Snap(Job(2, 10)), T0.AddSeconds(90));
        Assert.InRange(tracker.Active[0].Eta!.Value.TotalSeconds, 208, 212);
    }

    [Fact]
    public void EtaFallsBackToLearnedPrinterAverage()
    {
        var tracker = new JobTracker(name => name == Pr ? 6 : null);   // 6 pages per minute
        tracker.Update(Snap(Job(0, 12)), T0);
        Assert.InRange(tracker.Active[0].Eta!.Value.TotalSeconds, 118, 121);
    }

    [Fact]
    public void ObservedRateIsSmoothed()
    {
        var e = new EtaEstimator(T0);
        e.Observe(T0.AddSeconds(30), 1);   // 2 ppm
        e.Observe(T0.AddSeconds(40), 2);   // 6 ppm sample
        Assert.InRange(e.PagesPerMinute!.Value, 3.5, 3.7);   // 0.6*2 + 0.4*6
    }

    [Fact]
    public void LearnIgnoresTinyAndShortJobs()
    {
        Assert.Equal(5, EtaEstimator.Learn(5, 1, TimeSpan.FromMinutes(1)));
        Assert.Equal(5, EtaEstimator.Learn(5, 10, TimeSpan.FromSeconds(3)));
        Assert.Equal(6, EtaEstimator.Learn(null, 12, TimeSpan.FromMinutes(2)));
        Assert.Equal(0.7 * 6 + 0.3 * 12, EtaEstimator.Learn(6, 24, TimeSpan.FromMinutes(2))!.Value, 6);
    }

    [Fact]
    public void ZeroTotalPagesMeansNoPageCountsAndNoNonsense()
    {
        var tracker = new JobTracker(_ => 6);
        tracker.Update(Snap(Job(0, 0)), T0);
        var p = tracker.Active[0];
        Assert.False(p.PagesReported);
        Assert.Null(p.Fraction);
        Assert.Null(p.Eta);
        Assert.Equal(PrinterText.NoPageCounts, PrinterText.Pages(p));
        Assert.Equal("Printing", PrinterText.Pill(p));
    }

    [Fact]
    public void PagesPrintedStuckAtZeroWhilePrintingBecomesNotReported()
    {
        var tracker = new JobTracker(_ => null);
        tracker.Update(Snap(Job(0, 12)), T0);
        Assert.True(tracker.Active[0].PagesReported);
        tracker.Update(Snap(Job(0, 12)), T0 + JobTracker.PagesGrace + TimeSpan.FromSeconds(1));
        Assert.False(tracker.Active[0].PagesReported);
    }

    [Fact]
    public void ProgressIsClampedWhenPrintedExceedsTotal()
    {
        var tracker = new JobTracker(_ => null);
        tracker.Update(Snap(Job(15, 12)), T0);
        Assert.Equal(1, tracker.Active[0].Fraction);
        Assert.Equal("12 of 12 pages", PrinterText.Pages(tracker.Active[0]));
    }

    [Theory]
    [InlineData(JobFlags.Spooling, JobPhase.Spooling)]
    [InlineData(JobFlags.Spooling | JobFlags.Printing, JobPhase.Printing)]
    [InlineData(JobFlags.Paused, JobPhase.Paused)]
    [InlineData(JobFlags.Printing | JobFlags.PaperOut, JobPhase.NeedsAttention)]
    [InlineData(JobFlags.Error, JobPhase.NeedsAttention)]
    [InlineData(JobFlags.Deleting | JobFlags.Printing, JobPhase.Cancelling)]
    [InlineData(JobFlags.None, JobPhase.Waiting)]
    public void PhaseMapping(JobFlags flags, JobPhase phase) => Assert.Equal(phase, JobTracker.PhaseOf(flags));

    [Fact]
    public void FinishedJobCountsWholeDocumentWhenDriverNeverReportedPagesPrinted()
    {
        var tracker = new JobTracker(_ => null);
        tracker.Update(Snap(Job(0, 5)), T0);
        var ended = tracker.Update(Snap(), T0.AddMinutes(1));
        var end = Assert.Single(ended);
        Assert.Equal(JobOutcome.Completed, end.Outcome);
        Assert.Equal(5, end.Pages);
        Assert.Empty(tracker.Active);
    }

    [Fact]
    public void CancelledAndFailedJobsCountOnlyPrintedPages()
    {
        var tracker = new JobTracker(_ => null);
        tracker.Update(Snap(Job(2, 9, JobFlags.Printing | JobFlags.Deleting)), T0);
        var end = Assert.Single(tracker.Update(Snap(), T0.AddSeconds(5)));
        Assert.Equal(JobOutcome.Cancelled, end.Outcome);
        Assert.Equal(2, end.Pages);

        tracker.Update(Snap(Job(1, 9, JobFlags.Error)), T0.AddSeconds(10));
        end = Assert.Single(tracker.Update(Snap(), T0.AddSeconds(15)));
        Assert.Equal(JobOutcome.Failed, end.Outcome);
        Assert.Equal(1, end.Pages);
    }

    [Fact]
    public void PillAndEtaText()
    {
        var job = new JobProgress(1, Pr, "d", JobPhase.Printing, 3, 12, 0.25, TimeSpan.FromMinutes(1.5), true);
        Assert.Equal("1:30", PrinterText.Pill(job));
        Assert.Equal("Estimating", PrinterText.Pill(job with { Eta = null }));
        Assert.Equal("25%", PrinterText.PillPercent(job.Fraction));
        Assert.Equal("", PrinterText.PillPercent(null));
        Assert.Equal("About 2 min left", PrinterText.EtaLine(job));
        Assert.Equal("Estimating…", PrinterText.EtaLine(job with { Eta = null }));
        Assert.Equal("under 1 min", PrinterText.Eta(TimeSpan.FromSeconds(20)));
        Assert.Equal("1 h 5 min", PrinterText.Eta(TimeSpan.FromMinutes(65)));
        Assert.Equal("Print job", PrinterText.DocumentName("secret.pdf", hide: true));
        Assert.Equal("secret.pdf", PrinterText.DocumentName("secret.pdf", hide: false));
    }

    [Fact]
    public void PadResetDisplayName() => Assert.Equal("Reset", PrinterText.ActionName(PrinterAction.ResetPad));
}
