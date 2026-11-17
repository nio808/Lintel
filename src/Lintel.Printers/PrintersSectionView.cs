// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lintel.Core.Controls;
using Lintel.Core.Notch;
using Lintel.Core.Permissions;
using Lintel.Core.Text;

namespace Lintel.Printers;

/// <summary>Tiny element factories that reference the notch theme by resource key (the theme lives in the host window, not App).</summary>
internal static class Ui
{
    public static TextBlock Text(string text = "", string style = "Notch.Body", string? foreground = null)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        block.SetResourceReference(FrameworkElement.StyleProperty, style);
        if (foreground is not null)
            block.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        return block;
    }

    public static TextBlock Icon(string glyph, string? foreground = null)
    {
        var block = new TextBlock { Text = glyph };
        block.SetResourceReference(FrameworkElement.StyleProperty, "Notch.Icon");
        if (foreground is not null)
            block.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        return block;
    }

    public static Button Pill(string text, Action click)
    {
        var button = new Button { Content = text };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Notch.PillButton");
        button.Click += (_, _) => click();
        return button;
    }

    public static Button IconButton(string glyph, string tip, Action click)
    {
        var button = new Button { Content = glyph, ToolTip = tip };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Notch.IconButton");
        button.Click += (_, _) => click();
        return button;
    }

    public static void Highlight(Button button, bool on)
    {
        if (on)
        {
            button.SetResourceReference(Control.BackgroundProperty, "Notch.Accent");
            button.SetResourceReference(Control.ForegroundProperty, "Notch.Background");
        }
        else
        {
            button.ClearValue(Control.BackgroundProperty);
            button.ClearValue(Control.ForegroundProperty);
        }
    }

    public static Border Card(UIElement child)
    {
        var card = new Border { Child = child };
        card.SetResourceReference(FrameworkElement.StyleProperty, "Notch.Card");
        return card;
    }

    public static string Tone(Severity s) => s switch { Severity.Critical => "Tone.Critical", Severity.Warning => "Tone.Warning", _ => "Notch.TextSecondary" };
}

/// <summary>Left card: the printer and its ink pad. Right card: the live job. Bottom strip: the recommendation and the buttons.</summary>
internal sealed class PrintersSectionView : Grid, ISectionLifecycle
{
    private static readonly (PrinterAction Action, string Label)[] Buttons =
        [(PrinterAction.NozzleCheck, "Nozzle check"), (PrinterAction.HeadClean, "Head clean"), (PrinterAction.PowerClean, "Power clean")];

    private readonly PrintersModule module;
    private readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Grid cards = new();
    private readonly TextBlock empty = Ui.Text("No printers found. Add one in Windows Settings > Printers & scanners.", "Notch.Body", "Notch.TextSecondary");

    // left
    private readonly TextBlock name = Ui.Text("", "Notch.Title");
    private readonly TextBlock chipText = Ui.Text("", "Notch.Caption");
    private readonly Border chip = new() { CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button prev, next;
    private readonly TextBlock position = Ui.Text("", "Notch.Caption");
    private readonly RingGauge ring = new() { Width = 48, Height = 48, Thickness = 5, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock padValue = Ui.Text("", "Notch.Numeric");
    private readonly TextBlock padLabel = Ui.Text("Ink pad", "Notch.Caption");
    private readonly TextBlock padNote = Ui.Text("", "Notch.Caption", "Notch.TextSecondary");
    private readonly TextBlock detail = Ui.Text("", "Notch.Caption");
    private readonly TextBlock pages = Ui.Text("", "Notch.Caption");
    private readonly TextBlock reset = Ui.Text("", "Notch.Caption");

    // right
    private readonly TextBlock jobTitle = Ui.Text("", "Notch.Title");
    private readonly TextBlock jobState = Ui.Text("", "Notch.Caption");
    private readonly TextBlock jobPages = Ui.Text("", "Notch.Numeric");
    private readonly MeterBar bar = new() { Margin = new Thickness(0, 6, 0, 6) };
    private readonly TextBlock jobEta = Ui.Text("", "Notch.Caption");

    // strip
    private readonly TextBlock recIcon = Ui.Icon(Glyphs.Info);
    private readonly TextBlock recText = Ui.Text("", "Notch.Body");
    private readonly TextBlock hint = Ui.Text("", "Notch.Caption", "Tone.Warning");
    private readonly MeterBar busyBar = new() { Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed, IsIndeterminate = true };
    private readonly Dictionary<PrinterAction, Button> buttons = [];
    private readonly Button service;
    private readonly Button resetButton;
    private readonly StackPanel serviceRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock serviceNote = Ui.Text("Only after the ink pads were replaced or cleaned.", "Notch.Caption", "Notch.TextSecondary");
    private readonly TextBlock noMaintenance = Ui.Text("", "Notch.Caption", "Notch.TextSecondary");
    private readonly StackPanel buttonRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
    private bool shown;
    private string? previousPrinter;
    private double lastFraction = -1;

    public PrintersSectionView(PrintersModule module)
    {
        this.module = module;
        Margin = new Thickness(0, 2, 0, 0);
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // cards
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });

        chip.Child = chipText;
        chip.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        prev = Ui.IconButton(Glyphs.ChevronLeft, "Previous printer", () => Step(-1));
        next = Ui.IconButton(Glyphs.ChevronRight, "Next printer", () => Step(1));

        var head = new DockPanel { LastChildFill = true };
        var selector = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        selector.Children.Add(prev);
        selector.Children.Add(position);
        selector.Children.Add(next);
        DockPanel.SetDock(selector, Dock.Right);
        head.Children.Add(selector);
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(name);
        title.Children.Add(chip);
        head.Children.Add(title);
        selectorPanel = selector;

        padValue.HorizontalAlignment = HorizontalAlignment.Center;
        var ringHost = new Grid { Width = 48, Height = 48, Margin = new Thickness(0, 0, 10, 0) };
        ringHost.Children.Add(ring);
        var padInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        padInfo.Children.Add(padLabel);
        padInfo.Children.Add(padValue);
        padInfo.Children.Add(padNote);
        padValue.HorizontalAlignment = HorizontalAlignment.Left;
        var padRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        padRow.Children.Add(ringHost);
        padRow.Children.Add(padInfo);
        padNote.TextWrapping = TextWrapping.Wrap;
        padNote.MaxWidth = 150;

        var left = new StackPanel();
        left.Children.Add(head);
        left.Children.Add(padRow);
        left.Children.Add(detail);
        left.Children.Add(pages);
        left.Children.Add(reset);
        var leftCard = Ui.Card(left);
        Grid.SetColumn(leftCard, 0);
        cards.Children.Add(leftCard);

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(jobTitle);
        right.Children.Add(jobState);
        jobPages.Margin = new Thickness(0, 8, 0, 0);
        jobPages.TextWrapping = TextWrapping.Wrap;
        right.Children.Add(jobPages);
        right.Children.Add(bar);
        right.Children.Add(jobEta);
        bar.SetResourceReference(MeterBar.FillProperty, "Notch.Accent");
        var rightCard = Ui.Card(right);
        Grid.SetColumn(rightCard, 2);
        cards.Children.Add(rightCard);

        Children.Add(cards);
        empty.TextWrapping = TextWrapping.Wrap;
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        empty.Visibility = Visibility.Collapsed;
        Children.Add(empty);

        // strip
        recText.TextWrapping = TextWrapping.Wrap;
        recIcon.Margin = new Thickness(0, 0, 6, 0);
        recIcon.VerticalAlignment = VerticalAlignment.Top;
        var recRow = new DockPanel();
        DockPanel.SetDock(recIcon, Dock.Left);
        recRow.Children.Add(recIcon);
        recRow.Children.Add(recText);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Margin = new Thickness(0, 3, 0, 0);
        hint.Visibility = Visibility.Collapsed;
        foreach (var (action, label) in Buttons)
        {
            var captured = action;
            var button = Ui.Pill(label, () => _ = Click(captured));
            button.Margin = new Thickness(0, 0, 4, 0);
            ToolTipService.SetShowOnDisabled(button, true);
            buttons[action] = button;
            buttonRow.Children.Add(button);
        }
        service = Ui.Pill("Service", ToggleService);
        service.Margin = new Thickness(8, 0, 0, 0);
        buttonRow.Children.Add(service);
        buttonRow.Children.Add(noMaintenance);
        noMaintenance.Margin = new Thickness(8, 0, 0, 0);
        resetButton = Ui.Pill("Reset pad counter", () => _ = Click(PrinterAction.ResetPad));
        ToolTipService.SetShowOnDisabled(resetButton, true);
        serviceNote.Margin = new Thickness(8, 0, 0, 0);
        serviceRow.Children.Add(resetButton);
        serviceRow.Children.Add(serviceNote);

        var strip = new StackPanel();
        strip.Children.Add(recRow);
        strip.Children.Add(hint);
        strip.Children.Add(busyBar);
        strip.Children.Add(buttonRow);
        strip.Children.Add(serviceRow);
        var stripCard = Ui.Card(strip);
        stripCard.Margin = new Thickness(0, 6, 0, 0);
        SetRow(stripCard, 1);
        Children.Add(stripCard);
        stripHost = stripCard;

        tick.Tick += (_, _) => Update();
        module.Updated += OnUpdated;
        Unloaded += (_, _) => module.Updated -= OnUpdated;
        Loaded += (_, _) =>
        {
            module.Updated -= OnUpdated;
            module.Updated += OnUpdated;
        };
        Update();
    }

    private readonly StackPanel selectorPanel;
    private readonly Border stripHost;

    public void OnShown()
    {
        shown = true;
        module.ViewShown();
        Update();
    }

    public void OnHidden()
    {
        shown = false;
        tick.Stop();
        module.Controller.Disarm();
        module.ViewHidden();
    }

    private void OnUpdated()
    {
        if (shown || !IsLoaded)
            Update();
    }

    private void Step(int delta)
    {
        var list = module.Printers;
        if (list.Count < 2)
            return;
        var i = list.ToList().FindIndex(p => p.Name == module.Selected?.Name);
        module.Controller.Disarm();
        module.SelectPrinter(list[(i + delta + list.Count) % list.Count].Name);
    }

    private void ToggleService() =>
        serviceRow.Visibility = serviceRow.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private async Task Click(PrinterAction action)
    {
        await module.ClickAsync(action);
        Update();
    }

    private void Update()
    {
        var printer = module.Selected;
        cards.Visibility = stripHost.Visibility = printer is null ? Visibility.Collapsed : Visibility.Visible;
        empty.Visibility = printer is null ? Visibility.Visible : Visibility.Collapsed;
        if (printer is null)
        {
            tick.Stop();
            return;
        }
        if (previousPrinter != printer.Name)
        {
            previousPrinter = printer.Name;
            lastFraction = -1;
        }
        var now = module.Context.Time.GetUtcNow();
        var s = module.StatsOf(printer.Name);
        var job = module.JobOf(printer.Name);
        var hide = module.HideNames;

        // left card
        var count = module.Printers.Count;
        selectorPanel.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        position.Text = $"{module.Printers.ToList().FindIndex(p => p.Name == printer.Name) + 1}/{count}";
        name.Text = printer.Name;
        name.ToolTip = printer.Name;
        chipText.Text = PrinterRules.Describe(printer.State);
        chipText.SetResourceReference(TextBlock.ForegroundProperty, printer.State switch
        {
            PrinterState.Ready => "Tone.Success", PrinterState.Printing => "Notch.Accent", PrinterState.Paused => "Tone.Warning", _ => "Tone.Critical",
        });
        detail.Text = $"{printer.Driver} · {printer.Port} ({PrinterRules.Describe(printer.Kind)})";
        pages.Text = $"{s.LifetimePages:N0} pages seen · {s.PagesSinceHeadClean:N0} since head clean";
        reset.Text = s.LastPadReset is { } r ? $"Pads reset {PrinterText.ShortDate(r)}" + (s.PadPercentBeforeReset is { } b ? $" · was {Format.Percent(b)}" : "") : "";
        reset.Visibility = reset.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdatePad(printer, s);

        // right card
        UpdateJob(printer, job, s, now, hide);

        // strip
        UpdateStrip(printer, job, s, now);

        var needTick = shown && (job is not null || module.Controller.Running(printer.Name) is not null || buttons.Keys.Concat([PrinterAction.ResetPad]).Any(a => module.Controller.IsArmed(printer.Name, a)));
        if (needTick && !tick.IsEnabled) tick.Start();
        else if (!needTick && tick.IsEnabled) tick.Stop();
    }

    private void UpdatePad(PrinterInfo printer, PrinterStats s)
    {
        var device = module.Device;
        var limits = module.LimitsOf(printer);
        if (!device.CanReadPad(printer.Driver))
        {
            SetPad(null, "Not available for this model", "Notch.TextTertiary");
            return;
        }
        if (!module.Granted(PermissionIds.PrinterMaintenance))
        {
            SetPad(null, "Turn on Printer maintenance in Settings to read it", "Notch.TextTertiary");
            return;
        }
        if (module.PadPercent(printer) is not { } percent)
        {
            // Not read now (printing, offline, first open): show the cached value from the last read, if any.
            var busy = module.JobOf(printer.Name) is not null;
            var why = module.IsReadingPad(printer.Name) ? "Reading…" : busy ? "Not read while printing" : !printer.IsOnline ? "Printer is offline" : "Not read yet";
            if (s.LastPadPercent is { } cached && s.LastPadRead is { } at)
            {
                var stale = RecommendationEngine.Pad(cached, module.Thresholds);
                SetPad(cached, $"{why} · last read {PrinterText.Ago(at, module.Context.Time.GetUtcNow())}", stale.Level is { } sl ? Ui.Tone(sl) : "Notch.Accent");
            }
            else
            {
                SetPad(null, why, "Notch.TextTertiary");
            }
            return;
        }
        var level = RecommendationEngine.Pad(percent, module.Thresholds);
        var raw = module.PadOf(printer.Name)!;
        var note = level.Level == Severity.Critical ? "Service soon" : level.Level == Severity.Warning ? "Filling up"
            : limits?.PadCounterMax is { } max ? $"{raw.MainRaw:N0} of {max:N0}" : "";
        SetPad(percent, note, level.Level is { } l ? Ui.Tone(l) : "Notch.Accent");
    }

    private void SetPad(double? percent, string note, string brush)
    {
        ring.Value = percent ?? 0;
        ring.SetResourceReference(RingGauge.StrokeProperty, brush == "Notch.TextTertiary" ? "Notch.TextTertiary" : brush);
        padValue.Text = percent is { } q ? Format.Percent(q) : "";
        padValue.Visibility = percent is null ? Visibility.Collapsed : Visibility.Visible;
        padNote.Text = note;
        padNote.Visibility = note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateJob(PrinterInfo printer, JobProgress? job, PrinterStats s, DateTimeOffset now, bool hide)
    {
        if (job is null)
        {
            jobTitle.Text = printer.State == PrinterState.Ready ? "Idle" : PrinterRules.Describe(printer.State);
            jobState.Text = $"Last printed {PrinterText.Ago(s.LastPrint, now)}";
            jobPages.Visibility = bar.Visibility = jobEta.Visibility = Visibility.Collapsed;
            lastFraction = -1;
            return;
        }
        jobPages.Visibility = bar.Visibility = jobEta.Visibility = Visibility.Visible;
        jobTitle.Text = PrinterText.DocumentName(job.Document, hide);
        jobState.Text = job.PhaseText;
        jobPages.Text = job.PagesReported ? PrinterText.Pages(job) : "";
        jobPages.Visibility = job.PagesReported ? Visibility.Visible : Visibility.Collapsed;
        jobEta.Text = job.PagesReported ? PrinterText.EtaLine(job) : PrinterText.NoPageCounts;
        bar.IsIndeterminate = job.Fraction is null;
        if (job.Fraction is { } f && Math.Abs(f - lastFraction) > 0.0001)
        {
            lastFraction = f;
            if (IsLoaded && SystemParameters.ClientAreaAnimation)
                bar.BeginAnimation(MeterBar.ValueProperty, new DoubleAnimation(f, TimeSpan.FromMilliseconds(400)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else
                bar.Value = f;
        }
    }

    private void UpdateStrip(PrinterInfo printer, JobProgress? job, PrinterStats s, DateTimeOffset now)
    {
        var rec = module.RecommendationOf(printer);
        var pad = RecommendationEngine.Pad(module.PadPercent(printer), module.Thresholds);
        var supported = module.Device.IsSupported(printer.Driver);
        // One line: the pad warning outranks a cleaning tip (cleaning fills the pad further).
        var (text, severity) = pad.Level is { } l && pad.Text is not null ? (pad.Text, l) : (rec.Text, rec.Severity);
        recText.Text = text;
        recIcon.Text = severity == Severity.Info ? Glyphs.Check : Glyphs.Warning;
        recIcon.SetResourceReference(TextBlock.ForegroundProperty, severity == Severity.Info ? "Tone.Success" : Ui.Tone(severity));

        var controller = module.Controller;
        var running = controller.Running(printer.Name);
        string? hintText = null;
        foreach (var (action, label) in Buttons)
        {
            var b = buttons[action];
            var state = controller.StateOf(PrinterActionOf(action), printer, job is not null);
            b.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
            b.IsEnabled = state.Enabled;
            b.ToolTip = state.Reason;
            b.Content = state.Armed ? ConfirmLabel(action) : label;
            Ui.Highlight(b, state.Armed || state.Enabled && rec.Highlight.Contains(Maintenance(action)) && pad.Level != Severity.Critical);
            if (state.Armed && MaintenanceController.ConfirmText(action) is { Length: > 0 } t)
                hintText = t;
        }
        var resetState = controller.StateOf(PrinterAction.ResetPad, printer, job is not null);
        resetButton.IsEnabled = resetState.Enabled;
        resetButton.ToolTip = resetState.Reason;
        resetButton.Content = resetState.Armed ? "Confirm reset?" : "Reset pad counter";
        Ui.Highlight(resetButton, resetState.Armed);
        if (resetState.Armed)
            hintText = MaintenanceController.ResetConfirm;
        service.Visibility = supported && module.Device.CanResetPad(printer.Driver) ? Visibility.Visible : Visibility.Collapsed;
        if (service.Visibility == Visibility.Collapsed)
            serviceRow.Visibility = Visibility.Collapsed;

        noMaintenance.Text = !supported ? "Maintenance commands are not available for this printer"
            : !module.Granted(PermissionIds.PrinterMaintenance) ? "Maintenance is off: turn it on in Settings > Permissions" : "";
        noMaintenance.Visibility = noMaintenance.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        busyBar.Visibility = running is null ? Visibility.Collapsed : Visibility.Visible;
        if (running is { } r)
            hintText = $"{ActionName(r.Action)} running · about {Format.Clock(r.Remaining)} left. Buttons stay off until the printer is done.";
        hint.Text = hintText ?? "";
        hint.Visibility = hintText is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static PrinterAction PrinterActionOf(PrinterAction a) => a;

    private static MaintenanceAction Maintenance(PrinterAction a) => a switch
    {
        PrinterAction.NozzleCheck => MaintenanceAction.NozzleCheck, PrinterAction.HeadClean => MaintenanceAction.HeadClean, _ => MaintenanceAction.PowerClean,
    };

    private static string ConfirmLabel(PrinterAction a) => a == PrinterAction.PowerClean ? "Confirm power clean?" : MaintenanceController.HeadCleanConfirm;

    private static string ActionName(PrinterAction a) => a switch
    {
        PrinterAction.NozzleCheck => "Nozzle check", PrinterAction.HeadClean => "Head clean", PrinterAction.PowerClean => "Power clean", _ => "Reset",
    };
}
