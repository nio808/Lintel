// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Lintel.Core.Text;

namespace Lintel.Printers;

/// <summary>Wording shared by the section, the pill and the notices.</summary>
public static class PrinterText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const string NoPageCounts = "Page counts not reported by this driver";

    public static string ActionName(PrinterAction action) => action switch
    {
        PrinterAction.NozzleCheck => "Nozzle check", PrinterAction.HeadClean => "Head clean", PrinterAction.PowerClean => "Power clean", _ => "Reset",
    };

    /// <summary>The collapsed pill's left wing: "42%"; empty when progress is unknown.</summary>
    public static string PillPercent(double? fraction) => fraction is { } f ? Format.Percent(Math.Clamp(f, 0, 1) * 100) : "";

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

    /// <summary>The collapsed pill's right wing: a countdown "2:41", or the state when there is no estimate ("Printing", "Paused").</summary>
    public static string Pill(JobProgress job) =>
        job.Phase is JobPhase.NeedsAttention or JobPhase.Paused or JobPhase.Cancelling || !job.PagesReported ? job.PhaseText
        : job.Eta is { } eta ? Format.Clock(eta) : "Estimating";

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
