// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Lintel.Core.Controls;
using Lintel.Core.Modules;
using Lintel.Core.Notch;
using Lintel.Core.Permissions;
using Lintel.Tools.Ui;

namespace Lintel.Tools.Toggles;

public sealed class TogglesModule : INotchModule, IToolsPanel
{
    private readonly Func<IReadOnlyList<IToggleAction>> actions;
    private bool started;

    public TogglesModule() : this(WindowsToggles.Create) { }

    internal TogglesModule(Func<IReadOnlyList<IToggleAction>> actions) => this.actions = actions;

    public ModuleManifest Manifest { get; } = new()
    {
        Id = "toggles",
        Name = "Quick Toggles",
        Summary = "Dark mode, desktop icons, mic mute, lock, screen snip, Recycle Bin and settings shortcuts.",
        Glyph = Glyphs.Toggle,
        Category = ModuleCategory.System,
        RequiredPermissions = [PermissionIds.SystemToggles],
        Order = 110,
    };

    public IReadOnlyList<NotchSection> Sections { get; private set; } = [];
    public IReadOnlyList<CompactActivity> Activities => [];
    internal TimeProvider Time { get; private set; } = TimeProvider.System;

    int IToolsPanel.Order => 1;
    double? IToolsPanel.Width => null;
    FrameworkElement IToolsPanel.Create() => new TogglesPanel(actions().Select(a => new ToggleTile(a, Time)).ToList());

    public void Start(IModuleContext context)
    {
        if (started)
            return;
        started = true;
        Time = context.Time;
        Sections = [ToolsSection.Create()];
        ToolsSection.Register(this);
    }

    public void Stop()
    {
        if (!started)
            return;
        started = false;
        ToolsSection.Unregister(this);
    }

    public FrameworkElement? CreateSettingsView() => null;

    public void DeleteData() { }
}

/// <summary>Right card of the Tools section: a grid of tiles. States are read when the section is shown and after each click; nothing polls.</summary>
internal sealed class TogglesPanel : Border, ISectionLifecycle
{
    private readonly List<(ToggleTile Tile, ToggleButton Button, TextBlock Label)> items = [];

    public TogglesPanel(IReadOnlyList<ToggleTile> tiles)
    {
        SetResourceReference(StyleProperty, "Notch.Card");
        var stack = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(Kit.Icon(Glyphs.Toggle, 12, "Notch.Accent"));
        var title = Kit.Text("Quick toggles", "Notch.Title");
        title.Margin = new Thickness(6, 0, 0, 0);
        header.Children.Add(title);
        stack.Children.Add(header);

        var grid = new UniformGrid { Columns = 5 };
        foreach (var tile in tiles)
            grid.Children.Add(Build(tile));
        stack.Children.Add(grid);
        Child = stack;
    }

    public void OnShown()
    {
        foreach (var (tile, _, _) in items)
            tile.Refresh();
    }

    public void OnHidden() { }

    private static void InheritForeground(TextBlock block) =>
        block.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Control.Foreground)) { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(ToggleButton), 1) });

    private ToggleButton Build(ToggleTile tile)
    {
        var icon = Kit.Icon(tile.Action.Glyph, 16);
        InheritForeground(icon);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var label = Kit.Text(tile.Action.Title, "Notch.Caption");
        InheritForeground(label);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.Margin = new Thickness(0, 3, 0, 0);
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(icon);
        content.Children.Add(label);
        var button = new ToggleButton { Content = content, Height = 46, Padding = new Thickness(2, 4, 2, 4), Margin = new Thickness(0, 0, 4, 4) };
        button.SetResourceReference(Control.BackgroundProperty, "Notch.SurfaceHover");
        var dispatcher = Dispatcher;
        DispatcherTimer? disarm = null;

        button.Click += (_, _) =>
        {
            tile.Click();
            Sync();
            if (tile.IsArmed)
            {
                disarm?.Stop();
                disarm = new DispatcherTimer(TimeSpan.FromSeconds(3) + TimeSpan.FromMilliseconds(50), DispatcherPriority.Normal, (s, _) =>
                {
                    ((DispatcherTimer)s!).Stop();
                    tile.Disarm();
                }, dispatcher);
            }
        };
        tile.PropertyChanged += (_, _) => Sync();
        items.Add((tile, button, label));
        Sync();
        return button;

        void Sync()
        {
            button.IsChecked = tile.State == true;
            button.IsEnabled = tile.IsAvailable;
            label.Text = tile.IsArmed ? "Click again" : tile.Action.Title;
            if (tile.IsArmed)
            {
                button.SetResourceReference(Control.BackgroundProperty, "Tone.Warning");
                button.SetResourceReference(Control.ForegroundProperty, "Notch.Background");
            }
            else
            {
                button.SetResourceReference(Control.BackgroundProperty, "Notch.SurfaceHover");
                button.ClearValue(Control.ForegroundProperty);
            }
            button.ToolTip = tile.Error is { } error ? $"{tile.Action.Title}: {error}"
                : tile.Action.Kind == ToggleKind.Switch ? $"{tile.Action.Title}: {(tile.State == true ? "on" : tile.State == false ? "off" : "unavailable")}"
                : tile.Action.Kind == ToggleKind.Link ? $"Open {tile.Action.Title} settings" : tile.Action.Title;
        }
    }
}
