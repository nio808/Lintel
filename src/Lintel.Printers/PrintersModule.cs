// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Lintel.Core.Controls;
using Lintel.Core.Modules;
using Lintel.Core.Notch;
using Lintel.Core.Permissions;
using Lintel.Printers.Spooler;

namespace Lintel.Printers;

internal static class PrintersKeys
{
    public const string HideNames = "printers.hideNames", Hidden = "printers.hidden", IdleDays = "printers.idleDays",
        PagesBetweenCleans = "printers.pagesBetweenCleans", Selected = "printers.selected";
}

/// <summary>Printer detection, live print activity, page statistics and (Epson) maintenance. See docs/ARCHITECTURE.md.</summary>
public sealed class PrintersModule : INotchModule
{
    public const string Id = "printers";

    private readonly Func<IModuleContext, IPrinterSource>? sourceFactory;
    private readonly IPrinterMaintenance? deviceOverride;
    private IModuleContext? context;
    private IPrinterSource? source;
    private IPrinterMaintenance device = new NoMaintenance();
    private StatsStore stats = new(null);
    private JobTracker tracker = new(_ => null);
    private SpoolerSnapshot raw = SpoolerSnapshot.Empty;
    private SpoolerSnapshot snapshot = SpoolerSnapshot.Empty;
    private readonly PadReadPolicy padPolicy = new();
    private readonly Dictionary<string, PadReading> pads = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> reading = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? ticker;
    private PrintActivity? activity;
    private int shownViews;
    private bool started;

    public PrintersModule() { }

    internal PrintersModule(Func<IModuleContext, IPrinterSource> source, IPrinterMaintenance maintenance)
    {
        sourceFactory = source;
        deviceOverride = maintenance;
    }

    public ModuleManifest Manifest { get; } = new()
    {
        Id = Id,
        Name = "Printers",
        Summary = "Printer status, live print progress, page counts and cleaning advice; head clean and ink pad for Epson.",
        Glyph = Glyphs.Printer,
        Category = ModuleCategory.System,
        RequiredPermissions = [PermissionIds.PrinterQueue],
        OptionalPermissions = [PermissionIds.PrinterMaintenance, PermissionIds.PrinterPadReset],
        Order = 120,
    };

    public IReadOnlyList<NotchSection> Sections { get; private set; } = [];
    public IReadOnlyList<CompactActivity> Activities { get; private set; } = [];

    internal IModuleContext Context => context!;
    internal MaintenanceController Controller { get; private set; } = null!;
    internal IPrinterMaintenance Device => device;
    internal IReadOnlyList<PrinterInfo> Printers { get; private set; } = [];
    internal IReadOnlyList<JobProgress> Jobs => tracker.Active;
    internal string? SelectedName { get; private set; }
    internal event Action? Updated;

    internal bool HideNames => context!.Settings.Get(PrintersKeys.HideNames, false);
    internal bool Granted(string permission) => context!.Consent.IsGranted(permission);

    internal Thresholds Thresholds => new(context!.Settings.Get(PrintersKeys.IdleDays, 14), context.Settings.Get(PrintersKeys.PagesBetweenCleans, 500));

    internal PrinterInfo? Selected => Printers.FirstOrDefault(p => p.Name == SelectedName) ?? Printers.FirstOrDefault();
    internal JobProgress? JobOf(string printer) => Jobs.FirstOrDefault(j => j.Printer == printer);
    internal PrinterStats StatsOf(string printer) => stats.Get(printer);
    internal ModelLimits? LimitsOf(PrinterInfo p) => device.LimitsFor(p.Driver);
    internal PadReading? PadOf(string printer) => pads.GetValueOrDefault(printer);
    internal bool IsReadingPad(string printer) => reading.Contains(printer);

    /// <summary>Worst pad percent, judged against the model's limits when the device layer knows them.</summary>
    internal double? PadPercent(PrinterInfo p) => PadOf(p.Name) is { } r ? PadMath.Worst(r, LimitsOf(p)) : null;

    internal Recommendation RecommendationOf(PrinterInfo p) => RecommendationEngine.Evaluate(StatsOf(p.Name), Thresholds, context!.Time.GetUtcNow(), PadPercent(p));

    public void Start(IModuleContext ctx)
    {
        if (started)
            return;
        started = true;
        context = ctx;
        device = deviceOverride ?? (ctx.IsDemo ? new DemoMaintenance() : new EpsonMaintenanceAdapter());
        stats = new StatsStore(ctx.IsDemo ? null : ctx.Settings);
        if (ctx.IsDemo)
            stats.Seed(DemoSource.Epson, DemoSource.SampleStats(ctx.Time.GetUtcNow()));
        tracker = new JobTracker(name => stats.Get(name).LearnedPagesPerMinute);
        Controller = new MaintenanceController(ctx.Time, device, ctx.Consent.IsGranted);
        Controller.Completed += OnMaintenanceDone;
        activity = new PrintActivity();
        Activities = [activity];
        Sections =
        [
            new NotchSection
            {
                Id = Id, Title = "Printers", Glyph = Glyphs.Printer, Order = 120,
                PreferredSize = new Size(560, 250),
                CreateView = () => new PrintersSectionView(this),
            },
        ];
        ticker = new DispatcherTimer(DispatcherPriority.Background, ctx.Ui) { Interval = TimeSpan.FromSeconds(1) };
        ticker.Tick += (_, _) => Recompute(notify: true);
        source = sourceFactory?.Invoke(ctx) ?? (ctx.IsDemo ? new DemoSource() : new WinSpoolerSource());
        source.Changed += s => ctx.Ui.InvokeAsync(() => OnSnapshot(s));
        ctx.Settings.Changed += OnSetting;
        ctx.Consent.Changed += OnConsent;
        source.Start();
        // First picture without waiting for the worker thread.
        OnSnapshot(source.Read());
    }

    public void Stop()
    {
        if (!started)
            return;
        started = false;
        ticker?.Stop();
        context!.Settings.Changed -= OnSetting;
        context.Consent.Changed -= OnConsent;
        source?.Dispose();
        source = null;
        if (activity is not null)
            activity.IsActive = false;
        context.Settings.Flush();
    }

    public FrameworkElement? CreateSettingsView() => new PrintersSettingsView(() => context, () => stats);

    public void DeleteData() => context?.Settings.RemovePrefix("printers.");

    internal void SelectPrinter(string name)
    {
        SelectedName = name;
        context!.Settings.Set(PrintersKeys.Selected, name);
        Updated?.Invoke();
        ReadPadIfDue(Selected, force: false);
    }

    internal void ViewShown()
    {
        shownViews++;
        UpdateActivityMode();
        ReadPadIfDue(Selected, force: false);
    }

    internal void ViewHidden()
    {
        shownViews = Math.Max(0, shownViews - 1);
        UpdateActivityMode();
    }

    internal Task<ClickResult> ClickAsync(PrinterAction action)
    {
        var p = Selected;
        return p is null ? Task.FromResult(ClickResult.Refused) : RunClick(action, p);
    }

    private async Task<ClickResult> RunClick(PrinterAction action, PrinterInfo p)
    {
        Recompute(notify: false);
        var result = await Controller.ClickAsync(action, p, Jobs.Any(j => j.Printer == p.Name)).ConfigureAwait(true);
        Updated?.Invoke();
        return result;
    }

    private void OnSetting(string key)
    {
        if (key.StartsWith("printers.", StringComparison.Ordinal) && key != StatsStore.Key)
            context!.Ui.InvokeAsync(() => OnSnapshot(raw));
    }

    private void OnConsent(string id)
    {
        if (id is PermissionIds.PrinterMaintenance or PermissionIds.PrinterPadReset)
            context!.Ui.InvokeAsync(() => Updated?.Invoke());
    }

    private void OnSnapshot(SpoolerSnapshot all)
    {
        if (!started)
            return;
        raw = all;
        var hidden = context!.Settings.Get(PrintersKeys.Hidden, Array.Empty<string>());
        var printers = PrinterRules.Physical(all.Printers).Where(p => !hidden.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var names = printers.Select(p => p.Name).ToHashSet();
        snapshot = new SpoolerSnapshot(printers, all.Jobs.Where(j => names.Contains(j.Printer)).ToList());
        Printers = printers;
        SelectedName ??= context.Settings.Get(PrintersKeys.Selected, "");
        if (SelectedName is not null && !names.Contains(SelectedName))
            SelectedName = printers.FirstOrDefault()?.Name;
        Recompute(notify: false);
        Updated?.Invoke();
        if (!Jobs.Any() && Selected is { } p)
            ReadPadIfDue(p, force: false);
    }

    /// <summary>Re-evaluates the tracker (ETA counts down), handles finished jobs and the 1 s ticker.</summary>
    private void Recompute(bool notify)
    {
        var now = context!.Time.GetUtcNow();
        foreach (var end in tracker.Update(snapshot, now))
            OnJobEnded(end, now);
        foreach (var end in Controller.TakeFinished())
            if (end.Action != PrinterAction.ResetPad)
                context.Notch.ShowNotice(new NotchNotice
                {
                    Id = "printers.maintenance", Title = $"{PrinterText.ActionName(end.Action)} finished",
                    Message = end.Action == PrinterAction.NozzleCheck ? null : "Print a nozzle check to see the result.",
                    Glyph = Glyphs.Printer, Tone = NoticeTone.Success, TargetSectionId = Id,
                });
        var maintenance = Controller.Running();
        var busy = tracker.Active.Count > 0 || maintenance is not null;
        if (ticker is not null && ticker.IsEnabled != busy)
        {
            if (busy) ticker.Start();
            else ticker.Stop();
        }
        UpdateActivityMode();
        activity?.Update(tracker.Active.FirstOrDefault(), maintenance);
        if (notify)
            Updated?.Invoke();
    }

    private void UpdateActivityMode() => source?.SetActivity(shownViews > 0, tracker.Active.Count > 0);

    private void OnJobEnded(JobEnd end, DateTimeOffset now)
    {
        if (end.Outcome != JobOutcome.Cancelled || end.Pages > 0)
            stats.RecordJob(end.Printer, end.Pages, now, end.Duration);
        padPolicy.MarkDirty(end.Printer);
        var name = PrinterText.DocumentName(end.Document, HideNames);
        var (title, message, tone) = end.Outcome switch
        {
            JobOutcome.Completed => ("Printed", name, NoticeTone.Success),
            JobOutcome.Cancelled => ("Print cancelled", name, NoticeTone.Info),
            _ => ("Print failed", name, NoticeTone.Warning),
        };
        context!.Notch.ShowNotice(new NotchNotice
        {
            Id = "printers.job", Title = title, Message = message, Glyph = Glyphs.Printer, Tone = tone,
            Duration = TimeSpan.FromSeconds(end.Outcome == JobOutcome.Failed ? 8 : 4), TargetSectionId = Id,
            BalloonIfHidden = end.Outcome == JobOutcome.Failed,
        });
    }

    private void OnMaintenanceDone(ActionDone done)
    {
        var now = context!.Time.GetUtcNow();
        if (!done.Success)
        {
            context.Notch.ShowNotice(new NotchNotice
            {
                Id = "printers.maintenance", Title = "Command not sent", Message = "The printer did not accept it. Check that it is on and connected.",
                Glyph = Glyphs.Printer, Tone = NoticeTone.Warning, TargetSectionId = Id,
            });
            return;
        }
        var printer = Printers.FirstOrDefault(p => p.Name == done.Printer);
        switch (done.Action)
        {
            case PrinterAction.ResetPad:
                stats.RecordPadReset(done.Printer, printer is null ? null : PadPercent(printer), now);
                pads.Remove(done.Printer);
                break;
            case PrinterAction.NozzleCheck: stats.RecordMaintenance(done.Printer, MaintenanceAction.NozzleCheck, now); break;
            case PrinterAction.HeadClean: stats.RecordMaintenance(done.Printer, MaintenanceAction.HeadClean, now); break;
            default: stats.RecordMaintenance(done.Printer, MaintenanceAction.PowerClean, now); break;
        }
        // The printer keeps working for a while; look at the pad counter once it should be done.
        padPolicy.MarkDirty(done.Printer);
        var wait = MaintenanceController.Lockout(done.Action) + TimeSpan.FromSeconds(2);
        var timer = new DispatcherTimer(DispatcherPriority.Background, context.Ui) { Interval = wait };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (started && Printers.FirstOrDefault(p => p.Name == done.Printer) is { } p)
                ReadPadIfDue(p, force: true);
            Updated?.Invoke();
        };
        timer.Start();
        Recompute(notify: true);
    }

    /// <summary>Pad counter: only when the printer can be asked, no job is printing, and the cache is stale (10 min) or marked dirty.</summary>
    private void ReadPadIfDue(PrinterInfo? p, bool force)
    {
        if (!started || p is null || reading.Contains(p.Name) || !device.CanReadPad(p.Driver) || !Granted(PermissionIds.PrinterMaintenance) || !p.IsOnline)
            return;
        var now = context!.Time.GetUtcNow();
        if (force)
            padPolicy.MarkDirty(p.Name);
        if (!padPolicy.ShouldRead(p.Name, now, Jobs.Any(j => j.Printer == p.Name)))
            return;
        padPolicy.Done(p.Name, now);
        reading.Add(p.Name);
        _ = ReadPadAsync(p);
    }

    private async Task ReadPadAsync(PrinterInfo p)
    {
        PadReading? result = null;
        try
        {
            result = await Task.Run(() => device.ReadPadAsync(p.Name, CancellationToken.None)).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            context?.Log.Warn("Reading the ink pad counter failed", e);
        }
        reading.Remove(p.Name);
        if (!started)
            return;
        if (result is not null)
        {
            pads[p.Name] = result;
            stats.RecordPadRead(p.Name, PadMath.Worst(result, device.LimitsFor(p.Driver)), context!.Time.GetUtcNow());
        }
        Updated?.Invoke();
    }
}

/// <summary>Pill: a progress ring and "42%" on the left wing, a countdown "2:41" on the right.</summary>
internal sealed class PrintActivity : CompactActivity
{
    private RingGauge? ring;
    private TextBlock? label, percent;

    public PrintActivity() : base("printers.job", 75, PrintersModule.Id) { }

    public override FrameworkElement CreateLeading()
    {
        ring = new RingGauge { Width = 14, Height = 14, Thickness = 2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        ring.SetResourceReference(RingGauge.StrokeProperty, "Notch.Accent");
        percent = Caption();
        percent.Margin = new Thickness(5, 0, 0, 0);
        return new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Children = { ring, percent } };
    }

    public override FrameworkElement? CreateTrailing()
    {
        label = Caption();
        label.HorizontalAlignment = HorizontalAlignment.Right;
        return label;
    }

    private static TextBlock Caption()
    {
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        text.SetResourceReference(FrameworkElement.StyleProperty, "Notch.Caption");
        text.SetResourceReference(TextBlock.ForegroundProperty, "Notch.Text");
        return text;
    }

    public void Update(JobProgress? job, (string Printer, PrinterAction Action, TimeSpan Remaining)? maintenance)
    {
        IsActive = job is not null || maintenance is not null;
        if (!IsActive)
            return;
        double? fraction = job is not null ? job.Fraction
            : Math.Clamp(1 - maintenance!.Value.Remaining / MaintenanceController.Lockout(maintenance.Value.Action), 0, 1);
        if (ring is not null)
        {
            ring.Value = (fraction ?? 0.15) * 100;
            ring.SetResourceReference(RingGauge.StrokeProperty, job?.Phase == JobPhase.NeedsAttention ? "Tone.Warning" : "Notch.Accent");
        }
        if (percent is not null)
            percent.Text = PrinterText.PillPercent(fraction);
        if (label is not null)
            label.Text = job is not null ? PrinterText.Pill(job) : Lintel.Core.Text.Format.Clock(maintenance!.Value.Remaining);
    }
}
