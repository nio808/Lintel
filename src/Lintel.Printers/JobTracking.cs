// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Printers;

public enum JobPhase { Waiting, Spooling, Printing, Paused, NeedsAttention, Cancelling }

public enum JobOutcome { Completed, Failed, Cancelled }

public sealed record JobProgress(
    int JobId, string Printer, string Document, JobPhase Phase, int Printed, int Total,
    double? Fraction,            // null = unknown (drivers that report no page counts)
    TimeSpan? Eta,               // null = "estimating…"
    bool PagesReported)          // false = "page counts not reported by this driver"
{
    public string PhaseText => Phase switch
    {
        JobPhase.Spooling => "Spooling", JobPhase.Printing => "Printing", JobPhase.Paused => "Paused",
        JobPhase.NeedsAttention => "Needs attention", JobPhase.Cancelling => "Cancelling", _ => "Waiting",
    };
}

public sealed record JobEnd(string Printer, string Document, JobOutcome Outcome, int Pages, TimeSpan Duration);

/// <summary>Pages per minute observed for one job; smoothed so one fast page does not swing the estimate.</summary>
public sealed class EtaEstimator
{
    private const double Smoothing = 0.4;
    private DateTimeOffset lastChange;
    private int lastPages;
    private double? rate;   // pages per minute

    public EtaEstimator(DateTimeOffset start) => lastChange = start;

    public double? PagesPerMinute => rate;

    public void Observe(DateTimeOffset now, int printed)
    {
        if (printed <= lastPages)
            return;
        var minutes = (now - lastChange).TotalMinutes;
        if (minutes > 0)
        {
            var sample = (printed - lastPages) / minutes;
            rate = rate is { } r ? r * (1 - Smoothing) + sample * Smoothing : sample;
        }
        lastPages = printed;
        lastChange = now;
    }

    /// <summary>Own observation once a page completed, else the printer's learned average, else null.</summary>
    public TimeSpan? Remaining(DateTimeOffset now, int printed, int total, double? learnedPpm)
    {
        if (total <= 0)
            return null;
        var ppm = rate ?? learnedPpm;   // own observation once a page completed in time, else the printer average
        if (ppm is not > 0)
            return null;
        // Counts down between page changes; stays at zero when a page is overdue.
        return TimeSpan.FromMinutes(Math.Max(0, Math.Max(0, total - printed) / ppm.Value - (now - lastChange).TotalMinutes));
    }

    /// <summary>New per-printer average after a finished job; short or tiny jobs say nothing about speed.</summary>
    public static double? Learn(double? previous, int pages, TimeSpan duration)
    {
        if (pages < 2 || duration < TimeSpan.FromSeconds(10))
            return previous;
        var sample = pages / duration.TotalMinutes;
        return previous is { } p ? p * 0.7 + sample * 0.3 : sample;
    }
}

/// <summary>Turns snapshots into live progress and job-ended events. Pure: time comes from the caller.</summary>
public sealed class JobTracker(Func<string, double?> learnedPpm)
{
    private sealed class Tracked(JobInfo first, DateTimeOffset now)
    {
        public JobInfo Last = first;
        public readonly DateTimeOffset Seen = now;
        public readonly EtaEstimator Eta = new(now);
        public bool SawPages;
        public DateTimeOffset? PrintingSince;
    }

    /// <summary>Pages never moved while printing for this long: assume the driver does not report them.</summary>
    public static readonly TimeSpan PagesGrace = TimeSpan.FromSeconds(45);

    private readonly Dictionary<(string, int), Tracked> jobs = [];

    public IReadOnlyList<JobProgress> Active { get; private set; } = [];

    public IReadOnlyList<JobEnd> Update(SpoolerSnapshot snapshot, DateTimeOffset now)
    {
        var ended = new List<JobEnd>();
        var present = new HashSet<(string, int)>();
        foreach (var job in snapshot.Jobs)
        {
            var key = (job.Printer, job.Id);
            present.Add(key);
            if (!jobs.TryGetValue(key, out var t))
                jobs[key] = t = new Tracked(job, now);
            t.Last = job;
            if (job.PagesPrinted > 0)
                t.SawPages = true;
            if (job.Flags.HasFlag(JobFlags.Printing))
                t.PrintingSince ??= now;
            t.Eta.Observe(now, job.PagesPrinted);
        }
        foreach (var key in jobs.Keys.Where(k => !present.Contains(k)).ToList())
        {
            var t = jobs[key];
            jobs.Remove(key);
            var f = t.Last.Flags;
            var outcome = f.HasFlag(JobFlags.Deleting) || f.HasFlag(JobFlags.Deleted) ? JobOutcome.Cancelled
                : f.HasFlag(JobFlags.Error) || f.HasFlag(JobFlags.PaperOut) || f.HasFlag(JobFlags.Offline) ? JobOutcome.Failed : JobOutcome.Completed;
            ended.Add(new JobEnd(t.Last.Printer, t.Last.Document, outcome, PagesDone(t.Last, outcome), now - t.Seen));
        }
        Active = snapshot.Jobs.Select(j => Progress(j, jobs[(j.Printer, j.Id)], now)).ToList();
        return ended;
    }

    /// <summary>Pages that really reached paper: a clean finish counts the whole document when the driver knew its length.</summary>
    public static int PagesDone(JobInfo last, JobOutcome outcome) =>
        outcome == JobOutcome.Completed ? Math.Max(last.PagesPrinted, last.TotalPages) : last.PagesPrinted;

    public static JobPhase PhaseOf(JobFlags f) =>
        f.HasFlag(JobFlags.Deleting) ? JobPhase.Cancelling
        : f.HasFlag(JobFlags.Error) || f.HasFlag(JobFlags.Offline) || f.HasFlag(JobFlags.PaperOut) || f.HasFlag(JobFlags.UserIntervention) || f.HasFlag(JobFlags.BlockedDevQueue) ? JobPhase.NeedsAttention
        : f.HasFlag(JobFlags.Paused) ? JobPhase.Paused
        : f.HasFlag(JobFlags.Printing) ? JobPhase.Printing
        : f.HasFlag(JobFlags.Spooling) ? JobPhase.Spooling : JobPhase.Waiting;

    private JobProgress Progress(JobInfo j, Tracked t, DateTimeOffset now)
    {
        var total = Math.Max(0, j.TotalPages);
        var printed = Math.Max(0, j.PagesPrinted);
        var stalled = !t.SawPages && t.PrintingSince is { } since && now - since > PagesGrace;
        var reported = total > 0 && !stalled;
        double? fraction = reported ? Math.Min(printed, total) / (double)total : null;
        var eta = reported ? t.Eta.Remaining(now, printed, total, learnedPpm(j.Printer)) : null;
        return new JobProgress(j.Id, j.Printer, j.Document, PhaseOf(j.Flags), printed, total, fraction, eta, reported);
    }
}
