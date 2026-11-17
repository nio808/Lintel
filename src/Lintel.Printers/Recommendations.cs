// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Lintel.Printers;

public enum Severity { Info, Warning, Critical }

public sealed record Thresholds(int IdleDays = 14, int PagesBetweenCleans = 500, double PadWarnPercent = 80, double PadCriticalPercent = 95)
{
    public static readonly TimeSpan PowerCleanWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan PowerCleanCooldown = TimeSpan.FromHours(12);
    public const int HeadCleansBeforePower = 3;
}

/// <summary>One line: what to do, why, and which buttons to highlight.</summary>
public sealed record Recommendation(string Text, Severity Severity, IReadOnlyList<MaintenanceAction> Highlight);

public sealed record PadAssessment(Severity? Level, string? Text);

public static class RecommendationEngine
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static Recommendation Evaluate(PrinterStats s, Thresholds t, DateTimeOffset now, double? padPercent = null)
    {
        var recent = s.RecentHeadCleans.Count(c => now - c <= Thresholds.PowerCleanWindow);
        var powerCooling = s.LastPowerClean is { } lp && now - lp < Thresholds.PowerCleanCooldown;

        if (recent >= Thresholds.HeadCleansBeforePower)
        {
            if (powerCooling)
                return new($"{recent} head cleans in 24 h, but a power clean ran {Hours(now - s.LastPowerClean!.Value)} ago · give it 12 h, then print a test page", Severity.Warning, []);
            if (padPercent is { } p && p >= t.PadCriticalPercent)
                return new($"{recent} head cleans did not help, but the ink pad is at {Pct(p)} · have the printer serviced instead of power cleaning", Severity.Critical, []);
            return new($"{recent} head cleans in 24 h did not fix it · power clean uses a lot of ink and fills the ink pad", Severity.Warning, [MaintenanceAction.PowerClean]);
        }

        // Idle time counts from the last print or clean, whichever is newer.
        var last = new[] { s.LastPrint, s.LastHeadClean, s.LastPowerClean }.Max();
        if (last is { } l && (now - l).TotalDays >= t.IdleDays)
            return new($"Not printed for {(int)(now - l).TotalDays} days · limit {t.IdleDays} · check the nozzles, then head clean if lines are missing",
                Severity.Warning, [MaintenanceAction.NozzleCheck, MaintenanceAction.HeadClean]);

        if (s.PagesSinceHeadClean >= t.PagesBetweenCleans)
            return new($"{s.PagesSinceHeadClean} pages since last clean · limit {t.PagesBetweenCleans} · check the nozzles, then head clean if needed",
                Severity.Warning, [MaintenanceAction.NozzleCheck, MaintenanceAction.HeadClean]);

        return new($"No cleaning needed · {s.PagesSinceHeadClean} pages since last clean · limit {t.PagesBetweenCleans}", Severity.Info, []);
    }

    public static PadAssessment Pad(double? percent, Thresholds t)
    {
        if (percent is not { } p)
            return new(null, null);
        if (p >= t.PadCriticalPercent)
            return new(Severity.Critical, $"Ink pad {Pct(p)} · service soon (limit {Pct(t.PadCriticalPercent)}): the pads need replacing");
        if (p >= t.PadWarnPercent)
            return new(Severity.Warning, $"Ink pad {Pct(p)} · filling up (warning at {Pct(t.PadWarnPercent)}): avoid extra cleaning");
        return new(null, null);
    }

    private static string Pct(double v) => Math.Round(v, MidpointRounding.AwayFromZero).ToString("0", Inv) + "%";

    private static string Hours(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h" : $"{Math.Max(1, (int)t.TotalMinutes)} min";
}

/// <summary>Spec: read on open and after each job/clean, cached, at most every 10 minutes by itself, never during a job.</summary>
public sealed class PadReadPolicy
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, DateTimeOffset> lastRead = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> dirty = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A job ended, a clean finished or the counter was reset: the cached value is stale.</summary>
    public void MarkDirty(string printer) => dirty.Add(printer);

    public bool ShouldRead(string printer, DateTimeOffset now, bool jobActive)
    {
        if (jobActive)
            return false;
        if (!lastRead.TryGetValue(printer, out var at))
            return true;
        return dirty.Contains(printer) || now - at >= MinInterval;
    }

    public void Done(string printer, DateTimeOffset now)
    {
        lastRead[printer] = now;
        dirty.Remove(printer);
    }
}
