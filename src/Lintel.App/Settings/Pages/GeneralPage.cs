// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Lintel.Core.Settings;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Lintel.App.Settings.Pages;

/// <summary>Behaviour of the notch (display, opening, fullscreen) and of the app (startup, updates, setup, render mode).</summary>
internal sealed class GeneralPage : SettingsPage
{
    private readonly ToggleSwitch visible;
    private readonly ToggleSwitch startup;
    private readonly ToggleSwitch updates;
    private readonly TextBlock visibleHint;
    private readonly List<FrameworkElement> hoverOnly = [];

    public GeneralPage(SettingsWindow host, AppServices s) : base(host, s, "General", "Lintel lives in the tray. Changes apply right away.")
    {
        visible = Ui.Toggle(!s.Notch.IsUserHidden, on => s.Notch.SetUserHidden(!on), "Show the notch");
        var showRow = Ui.Row(Ui.Tile(SymbolRegular.Eye24, Palette.Blue), "Show the notch", "Tray icon", visible);
        visibleHint = (TextBlock)((StackPanel)showRow.Children[1]).Children[1];
        startup = Ui.Toggle(s.StartupEnabled, s.SetStartup, "Start with Windows");
        updates = Ui.Toggle(s.UpdateChecksOn, s.SetUpdateChecks, "Check for updates");
        // The three switches people reach for, side by side.
        Body.Children.Add(Ui.Columns(3,
        [
            Ui.Card(showRow, new Thickness(0)),
            Ui.Card(Ui.Row(Ui.Tile(SymbolRegular.Rocket24, Palette.Coral), "Start with Windows", "Quietly, in the tray", startup), new Thickness(0)),
            Ui.Card(Ui.Row(Ui.Tile(SymbolRegular.ArrowSync24, Palette.Green), "Check for updates", "Daily · nothing sent", updates), new Thickness(0)),
        ]));

        var displays = new List<(string, string)> { ("primary", "Main display"), ("pointer", "Follow the pointer") };
        displays.AddRange(Displays.All().Select(d => (d.DeviceName, d.Label)));
        var display = Get(SettingKeys.NotchDisplay, "primary");
        if (!displays.Any(d => d.Item1 == display))
            displays.Add((display, $"{display} (not connected)"));

        var openDelay = Slider(0, 600, 20, s.Settings.Get(SettingKeys.NotchHoverDelayMs, 120), v => s.Settings.Set(SettingKeys.NotchHoverDelayMs, (int)v));
        var closeDelay = Slider(100, 1500, 50, s.Settings.Get(SettingKeys.NotchCollapseDelayMs, 350), v => s.Settings.Set(SettingKeys.NotchCollapseDelayMs, (int)v));
        var openDelayRow = Ui.Row(Ui.Tile(SymbolRegular.Timer24, Palette.Teal), "Open after", "Pointer rests on the pill", openDelay);
        hoverOnly.Add(openDelayRow);

        Body.Children.Add(Ui.Group("Notch",
        [
            Ui.Row(Ui.Tile(SymbolRegular.Desktop24, Palette.Indigo), "Display", "Where the notch sits",
                Ui.Choice(displays, display, v => s.Settings.Set(SettingKeys.NotchDisplay, v), 210)),
            Ui.Row(Ui.Tile(SymbolRegular.Cursor24, Palette.Purple), "Opens on", "Hover, or only when you click",
                Ui.Choice([("hover", "Hover"), ("click", "Click")], Get(SettingKeys.NotchExpandOn, "hover"), v =>
                {
                    s.Settings.Set(SettingKeys.NotchExpandOn, v);
                    UpdateHoverRows();
                })),
            openDelayRow,
            Ui.Row(Ui.Tile(SymbolRegular.Timer24, Palette.Slate), "Close after", "Grace after the pointer leaves", closeDelay),
            Ui.Row(Ui.Tile(SymbolRegular.Tv24, Palette.Orange), "Fullscreen apps", "Games, videos, slides on its display",
                Ui.Choice([("hide", "Hide the notch"), ("shrink", "Shrink to a line"), ("stay", "Keep it visible")], Get(SettingKeys.NotchFullscreen, "hide"),
                    v => s.Settings.Set(SettingKeys.NotchFullscreen, v))),
        ]));

        Body.Children.Add(Ui.Group("Advanced",
        [
            Ui.Row(Ui.Tile(SymbolRegular.Sparkle24, Palette.Pink), "Setup", "Pick a preset and review access again", Ui.Button("Run setup", host.ShowOnboarding)),
            Ui.Row(Ui.Tile(SymbolRegular.DeveloperBoard24, Palette.Gray), "Render mode", "Compatibility if the notch shows a box or flickers · after restart",
                Ui.Choice([("auto", "Automatic"), ("dwm", "GPU composited"), ("layered", "Compatibility")], Get(SettingKeys.NotchRenderMode, "auto"),
                    v => s.Settings.Set(SettingKeys.NotchRenderMode, v), 140)),
        ]));

        UpdateHoverRows();
        Refresh();
    }

    public override void Refresh()
    {
        Ui.Set(visible, !S.Notch.IsUserHidden);
        Ui.Set(startup, S.StartupEnabled);
        Ui.Set(updates, S.UpdateChecksOn);
        var gesture = S.Gesture("notch.hide");
        visibleHint.Text = gesture is null ? "Tray icon" : $"Tray · {gesture}";
    }

    private string Get(string key, string fallback) => S.Settings.Get(key, fallback);

    private void UpdateHoverRows()
    {
        var hover = Get(SettingKeys.NotchExpandOn, "hover") == "hover";
        foreach (var row in hoverOnly)
            row.IsEnabled = hover;
    }

    private static FrameworkElement Slider(double min, double max, double step, double value, Action<double> changed)
    {
        var label = Ui.Secondary($"{value:0} ms");
        label.MinWidth = 44;
        label.TextAlignment = TextAlignment.Right;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 8, 0);
        var slider = new Slider
        {
            Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true, SmallChange = step, LargeChange = step * 4,
            Value = Math.Clamp(value, min, max), Width = 130, VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, e) =>
        {
            label.Text = $"{e.NewValue:0} ms";
            changed(e.NewValue);
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(label);
        row.Children.Add(slider);
        return row;
    }
}
