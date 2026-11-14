// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Lintel.Core.Controls;
using Lintel.Core.Notch;
using Lintel.Core.Text;
using Lintel.Tools.Ui;

namespace Lintel.Tools.KeepAwake;

/// <summary>Left card of the Tools section: status, duration chips, "while app runs", options.</summary>
internal sealed class KeepAwakePanel : Border, ISectionLifecycle
{
    private static readonly (string Label, int Minutes)[] Durations = [("15m", 15), ("30m", 30), ("1h", 60), ("2h", 120)];

    private readonly KeepAwakeModule module;
    private readonly TextBlock status = Kit.Text("Off", "Notch.Body");
    private readonly TextBlock detail = Kit.Text("", "Notch.Caption");
    private readonly List<(ToggleButton Button, int? Minutes)> chips = [];
    private readonly ToggleButton appButton;
    private readonly CheckBox display = new() { Content = "Display on", Margin = new Thickness(0, 0, 0, 2) };
    private readonly CheckBox autoOff = new() { Content = "Off below 20% battery" };
    private bool shown;
    private bool syncing;

    public KeepAwakePanel(KeepAwakeModule module)
    {
        this.module = module;
        SetResourceReference(StyleProperty, "Notch.Card");

        var stack = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        header.Children.Add(Kit.Icon(Glyphs.Cup, 12, "Notch.Accent"));
        var title = Kit.Text("Keep awake", "Notch.Title");
        title.Margin = new Thickness(6, 0, 0, 0);
        header.Children.Add(title);
        stack.Children.Add(header);
        stack.Children.Add(status);
        detail.Margin = new Thickness(0, 1, 0, 6);
        stack.Children.Add(detail);

        var row = new UniformGrid { Columns = 5 };
        foreach (var (label, minutes) in Durations)
            row.Children.Add(Chip(label, minutes));
        row.Children.Add(Chip("∞", null));
        stack.Children.Add(row);

        appButton = Kit.PillToggle("While an app runs…", () => { });
        appButton.Click += OnAppClick;
        appButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        appButton.SetResourceReference(BackgroundProperty, "Notch.SurfaceHover");
        appButton.Margin = new Thickness(0, 4, 0, 6);
        stack.Children.Add(appButton);

        display.Checked += (_, _) => OptionChanged();
        display.Unchecked += (_, _) => OptionChanged();
        autoOff.Checked += (_, _) => OptionChanged();
        autoOff.Unchecked += (_, _) => OptionChanged();
        stack.Children.Add(display);
        stack.Children.Add(autoOff);
        Child = stack;
    }

    public void OnShown()
    {
        shown = true;
        module.Updated += Refresh;
        Refresh();
    }

    public void OnHidden()
    {
        shown = false;
        module.Updated -= Refresh;
    }

    private ToggleButton Chip(string label, int? minutes)
    {
        var chip = Kit.PillToggle(label, () => OnChip(minutes));
        chip.Padding = new Thickness(0, 2, 0, 2);
        chip.Margin = new Thickness(0, 0, 3, 0);
        chip.ToolTip = minutes is null ? "Until I turn it off" : "Keep awake for " + label;
        chips.Add((chip, minutes));
        return chip;
    }

    private void OnChip(int? minutes)
    {
        var active = module.Session is { } s && (minutes is null ? s.Kind == KeepAwakeKind.Indefinite : s.Kind == KeepAwakeKind.Timed && module.Minutes == minutes);
        if (active) module.End(null);
        else if (minutes is { } m) module.StartTimed(m);
        else module.StartIndefinite();
    }

    private void OnAppClick(object sender, RoutedEventArgs e)
    {
        if (module.Session?.Kind == KeepAwakeKind.WhileApp)
        {
            module.End(null);
            return;
        }
        appButton.IsChecked = false;
        var menu = new ContextMenu { PlacementTarget = appButton, Placement = PlacementMode.Top };
        foreach (var name in RunningApps())
        {
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => module.StartWhileApp(name);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No app windows found", IsEnabled = false });
        menu.IsOpen = true;
    }

    /// <summary>Distinct process names that own a visible window.</summary>
    private static IReadOnlyList<string> RunningApps()
    {
        var all = Process.GetProcesses();
        try
        {
            return all.Where(p => { try { return p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0; } catch (Exception) { return false; } })
                .Select(p => p.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        }
        finally
        {
            foreach (var p in all)
                p.Dispose();
        }
    }

    private void OptionChanged()
    {
        if (syncing)
            return;
        module.KeepDisplay = display.IsChecked == true;
        module.AutoOff = autoOff.IsChecked == true;
    }

    private void Refresh()
    {
        if (!shown)
            return;
        syncing = true;
        display.IsChecked = module.KeepDisplay;
        autoOff.IsChecked = module.AutoOff;
        syncing = false;

        var session = module.Session;
        var left = module.Remaining;
        status.Text = session is null ? "Off" : left is { } l ? $"On · {Format.Duration(TimeSpan.FromSeconds(Math.Ceiling(l.TotalSeconds)))} left"
            : session.Kind == KeepAwakeKind.WhileApp ? $"On · while {session.AppName}" : "On · until off";
        status.SetResourceReference(TextBlock.ForegroundProperty, session is null ? "Notch.TextSecondary" : "Tone.Success");
        detail.Text = session is null ? "PC sleeps as usual." : session.KeepDisplay ? "PC + display awake." : "PC awake; display may sleep.";
        foreach (var (chip, minutes) in chips)
            chip.IsChecked = session is not null && (minutes is null ? session.Kind == KeepAwakeKind.Indefinite : session.Kind == KeepAwakeKind.Timed && module.Minutes == minutes);
        appButton.IsChecked = session?.Kind == KeepAwakeKind.WhileApp;
        appButton.Content = session?.Kind == KeepAwakeKind.WhileApp ? $"While {session.AppName} · stop" : "While an app runs…";
    }
}
