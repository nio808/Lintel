// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Lintel.Printers;

/// <summary>Wording shared by the section, the pill and the notices.</summary>
public static class PrinterText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const string NoPageCounts = "Page counts not reported by this driver";

    /// <summary>"under 1 min", "2 min", "1 h 5 min".</summary>
    public static string Eta(TimeSpan t)
    {
        if (t.TotalSeconds < 45)
            return "under 1 min";
        var minutes = (int)Math.Ceiling(t.TotalMinutes);
        return minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min";
    }

    public static string EtaLine(JobProgress job) =>
        !job.PagesReported ? "" : job.Eta is { } eta ? "About " + Eta(eta) + " left" : "Estimating…";

    public static string Pages(JobProgress job) =>
        job.Total > 0 && job.PagesReported ? $"{Math.Min(job.Printed, job.Total)} of {job.Total} pages" : NoPageCounts;

    /// <summary>The collapsed pill's right wing: "3/12 · 2 min", "Printing", "Spooling".</summary>
    public static string Pill(JobProgress job)
    {
        if (job.Phase is JobPhase.NeedsAttention or JobPhase.Paused or JobPhase.Cancelling)
            return job.PhaseText;
        if (!job.PagesReported)
            return job.PhaseText;
        var pages = $"{Math.Min(job.Printed, job.Total)}/{job.Total}";
        return job.Eta is { } eta ? $"{pages} · {Eta(eta)}" : pages;
    }

    public static string DocumentName(string document, bool hide) =>
        hide || string.IsNullOrWhiteSpace(document) ? "Print job" : document;

    public static string Ago(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } t)
            return "never (since Lintel started watching)";
        var d = now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        return $"{(int)d.TotalDays} d ago";
    }

    public static string ShortDate(DateTimeOffset at) => at.ToLocalTime().ToString("d MMM", Inv);
}
