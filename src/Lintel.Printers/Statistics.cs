// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Core.Settings;

namespace Lintel.Printers;

/// <summary>Per-printer counters from what Lintel observed. Plain properties so System.Text.Json round-trips it.</summary>
public sealed class PrinterStats
{
    public long LifetimePages { get; set; }
    public long PagesSinceHeadClean { get; set; }
    public long PagesSincePowerClean { get; set; }
    public DateTimeOffset? LastPrint { get; set; }
    public DateTimeOffset? LastHeadClean { get; set; }
    public DateTimeOffset? LastPowerClean { get; set; }
    public DateTimeOffset? LastNozzleCheck { get; set; }
    public int HeadCleans { get; set; }
    public int PowerCleans { get; set; }
    public int NozzleChecks { get; set; }

    /// <summary>Head cleans since the last power clean (newest last, at most 10): the "did normal cleaning fix it" evidence.</summary>
    public List<DateTimeOffset> RecentHeadCleans { get; set; } = [];

    public double? LearnedPagesPerMinute { get; set; }
    public DateTimeOffset? LastPadReset { get; set; }
    public double? PadPercentBeforeReset { get; set; }
    public double? LastPadPercent { get; set; }
    public DateTimeOffset? LastPadRead { get; set; }
}

/// <summary>Statistics of all printers in one settings value ("printers.stats"); saved by the settings store's debounce.</summary>
public sealed class StatsStore(ISettingsStore? settings)
{
    public const string Key = "printers.stats";
    private Dictionary<string, PrinterStats>? all;

    private Dictionary<string, PrinterStats> All => all ??= new(settings?.Get(Key, new Dictionary<string, PrinterStats>()) ?? [], StringComparer.OrdinalIgnoreCase);

    public PrinterStats Get(string printer)
    {
        if (!All.TryGetValue(printer, out var s))
            All[printer] = s = new PrinterStats();
        return s;
    }

    public void RecordJob(string printer, int pages, DateTimeOffset now, TimeSpan? duration = null)
    {
        var s = Get(printer);
        s.LastPrint = now;
        if (pages > 0)
        {
            s.LifetimePages += pages;
            s.PagesSinceHeadClean += pages;
            s.PagesSincePowerClean += pages;
            s.LearnedPagesPerMinute = EtaEstimator.Learn(s.LearnedPagesPerMinute, pages, duration ?? TimeSpan.Zero);
        }
        Save();
    }

    /// <summary>A command was sent. A power clean also cleans the head, so it restarts both page counters and the head-clean streak.</summary>
    public void RecordMaintenance(string printer, MaintenanceAction action, DateTimeOffset now)
    {
        var s = Get(printer);
        switch (action)
        {
            case MaintenanceAction.NozzleCheck:
                s.NozzleChecks++;
                s.LastNozzleCheck = now;
                break;
            case MaintenanceAction.HeadClean:
                s.HeadCleans++;
                s.LastHeadClean = now;
                s.PagesSinceHeadClean = 0;
                s.RecentHeadCleans.Add(now);
                if (s.RecentHeadCleans.Count > 10)
                    s.RecentHeadCleans.RemoveAt(0);
                break;
            case MaintenanceAction.PowerClean:
                s.PowerCleans++;
                s.LastPowerClean = now;
                s.LastHeadClean = now;
                s.PagesSinceHeadClean = 0;
                s.PagesSincePowerClean = 0;
                s.RecentHeadCleans.Clear();
                break;
        }
        Save();
    }

    public void RecordPadRead(string printer, double percent, DateTimeOffset now)
    {
        var s = Get(printer);
        s.LastPadPercent = percent;
        s.LastPadRead = now;
        Save();
    }

    public void RecordPadReset(string printer, double? previousPercent, DateTimeOffset now)
    {
        var s = Get(printer);
        s.LastPadReset = now;
        s.PadPercentBeforeReset = previousPercent;
        s.LastPadPercent = 0;
        Save();
    }

    public void Seed(string printer, PrinterStats stats) => All[printer] = stats;

    public void Reset(string printer)
    {
        All.Remove(printer);
        Save();
    }

    public void ResetAll()
    {
        All.Clear();
        Save();
    }

    public void Save() => settings?.Set(Key, new Dictionary<string, PrinterStats>(All));
}
