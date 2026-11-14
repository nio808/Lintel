// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lintel.Core.Controls;
using Lintel.Core.Modules;
using Lintel.Core.Notch;

namespace Lintel.Notch.Tests;

/// <summary>Stand-in modules with plausible content: drive the shell in tests and in screenshots.</summary>
public static class DemoModules
{
    public static IReadOnlyList<ModuleDescriptor> Catalog() =>
    [
        Descriptor("agents", "AI Agents", 10, () => new DemoModule("agents",
            [new NotchSection { Id = "agents", Title = "AI", Glyph = Glyphs.Sparkle, Order = 10, PreferredSize = new Size(680, 300), CreateView = AgentsView }],
            [new DemoActivity("agents.quota", 20, "agents", QuotaRings, () => Label("62%"))])),
        Descriptor("media", "Now Playing", 20, () => new DemoModule("media",
            [new NotchSection { Id = "music", Title = "Music", Glyph = Glyphs.Music, Order = 20, PreferredSize = new Size(520, 168), CreateView = MusicView }],
            [new DemoActivity("media.nowplaying", 60, "music", Artwork, Equalizer)])),
        Descriptor("timer", "Timer", 40, () => new DemoModule("timer",
            [new NotchSection { Id = "timer", Title = "Timer", Glyph = Glyphs.Timer, Order = 40, PreferredSize = new Size(420, 180), CreateView = TimerView }],
            [new DemoActivity("timer.running", 80, "timer", TimerRing, () => Label("24:13"))])),
        Descriptor("keepawake", "Keep Awake", 100, () => new DemoModule("keepawake",
            [new NotchSection { Id = "tools", Title = "Tools", Glyph = Glyphs.Wrench, Order = 100, PreferredSize = new Size(500, 150), CreateView = ToolsView }], [])),
        Descriptor("toggles", "Quick Toggles", 110, () => new DemoModule("toggles",
            [new NotchSection { Id = "tools", Title = "Tools", Glyph = Glyphs.Wrench, Order = 110, PreferredSize = new Size(500, 150), CreateView = ToolsView }], [])),
    ];

    private static ModuleDescriptor Descriptor(string id, string name, int order, Func<INotchModule> create) =>
        new(new ModuleManifest { Id = id, Name = name, Summary = name, Glyph = Glyphs.Info, Category = ModuleCategory.Productivity, Order = order }, create);

    private static FrameworkElement AgentsView()
    {
        var grid = new UniformGrid2(2);
        grid.Add(Card("Claude · Max", ("Session · 5 h", 0.62, "Resets in 2h 14m", "Agent.Claude"), ("Weekly", 0.81, "Resets Thu 09:00", "Agent.Claude")));
        grid.Add(Card("Codex · Plus", ("Session · 5 h", 0.44, "Resets in 3h 02m", "Agent.Codex"), ("Weekly", 0.27, "Resets Mon 18:30", "Agent.Codex")));
        return new LifecycleView(grid.Root);
    }

    private static FrameworkElement MusicView()
    {
        var art = Artwork();
        art.Width = art.Height = 112;
        var title = Text("Midnight Drive", "Notch.Title");
        var artist = Text("Neon Harbor", "Notch.Caption");
        var progress = new MeterBar { Value = 0.38, Margin = new Thickness(0, 14, 0, 4) };
        progress.SetResourceReference(MeterBar.FillProperty, "Notch.Text");
        var times = new Grid();
        times.Children.Add(Text("1:24", "Notch.Caption"));
        times.Children.Add(Text("3:41", "Notch.Caption", HorizontalAlignment.Right));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var glyph in new[] { Glyphs.Shuffle, Glyphs.Previous, Glyphs.Pause, Glyphs.Next, Glyphs.Repeat })
        {
            var button = new Button { Content = glyph, Margin = new Thickness(4, 0, 4, 0) };
            button.SetResourceReference(FrameworkElement.StyleProperty, "Notch.IconButton");
            buttons.Children.Add(button);
        }
        var info = new StackPanel { Margin = new Thickness(16, 6, 0, 0), Children = { title, artist, progress, times, buttons } };
        var row = new DockPanel();
        DockPanel.SetDock(art, Dock.Left);
        row.Children.Add(art);
        row.Children.Add(info);
        return new LifecycleView(row);
    }

    private static FrameworkElement TimerView()
    {
        var ring = new RingGauge { Width = 120, Height = 120, Value = 62, Thickness = 6 };
        ring.SetResourceReference(RingGauge.StrokeProperty, "Notch.Accent");
        var time = Text("24:13", "Notch.Title", HorizontalAlignment.Center);
        time.FontSize = 26;
        time.VerticalAlignment = VerticalAlignment.Center;
        var dial = new Grid { Children = { ring, time } };
        var presets = new WrapPanel { Margin = new Thickness(20, 8, 0, 0), VerticalAlignment = VerticalAlignment.Center, Width = 220 };
        foreach (var p in new[] { "1 m", "5 m", "10 m", "15 m", "25 m", "45 m" })
        {
            var b = new Button { Content = p, Margin = new Thickness(0, 0, 6, 6) };
            b.SetResourceReference(FrameworkElement.StyleProperty, "Notch.PillButton");
            presets.Children.Add(b);
        }
        return new LifecycleView(new StackPanel { Orientation = Orientation.Horizontal, Children = { dial, presets } });
    }

    private static FrameworkElement ToolsView()
    {
        var grid = new UniformGrid2(4);
        foreach (var (glyph, label) in new[] { (Glyphs.Cup, "Keep awake"), (Glyphs.Moon, "Dark mode"), (Glyphs.Mic, "Mic"), (Glyphs.Lock, "Lock") })
        {
            var icon = Text(glyph, "Notch.Icon", HorizontalAlignment.Center);
            icon.FontSize = 20;
            var caption = Text(label, "Notch.Caption", HorizontalAlignment.Center);
            caption.Margin = new Thickness(0, 8, 0, 0);
            var tile = new Border { Height = 96, Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { icon, caption } } };
            tile.SetResourceReference(FrameworkElement.StyleProperty, "Notch.Card");
            grid.Add(tile);
        }
        return new LifecycleView(grid.Root);
    }

    private static Border Card(string title, params (string Label, double Remaining, string Reset, string Brush)[] rows)
    {
        var stack = new StackPanel();
        stack.Children.Add(Text(title, "Notch.Title"));
        foreach (var (label, remaining, reset, brush) in rows)
        {
            var head = new Grid { Margin = new Thickness(0, 14, 0, 6) };
            head.Children.Add(Text(label, "Notch.Body"));
            head.Children.Add(Text($"{remaining:P0} left", "Notch.Numeric", HorizontalAlignment.Right));
            var meter = new MeterBar { Value = remaining };
            meter.SetResourceReference(MeterBar.FillProperty, brush);
            var foot = Text(reset, "Notch.Caption");
            foot.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(head);
            stack.Children.Add(meter);
            stack.Children.Add(foot);
        }
        var card = new Border { Child = stack };
        card.SetResourceReference(FrameworkElement.StyleProperty, "Notch.Card");
        return card;
    }

    private static FrameworkElement QuotaRings()
    {
        var outer = new RingGauge { Width = 20, Height = 20, Value = 62, Thickness = 2.5 };
        outer.SetResourceReference(RingGauge.StrokeProperty, "Agent.Claude");
        var inner = new RingGauge { Width = 12, Height = 12, Value = 44, Thickness = 2.5 };
        inner.SetResourceReference(RingGauge.StrokeProperty, "Agent.Codex");
        return new Grid { Children = { outer, inner } };
    }

    private static FrameworkElement TimerRing()
    {
        var ring = new RingGauge { Width = 18, Height = 18, Value = 62, Thickness = 2.5 };
        ring.SetResourceReference(RingGauge.StrokeProperty, "Notch.Accent");
        return ring;
    }

    private static FrameworkElement Artwork()
    {
        var brush = new LinearGradientBrush(Color.FromRgb(0xFF, 0x7A, 0x59), Color.FromRgb(0x7C, 0x3A, 0xED), 45);
        brush.Freeze();
        return new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Background = brush };
    }

    private static FrameworkElement Equalizer()
    {
        var bars = new EqualizerBars { Width = 22, Height = 16, IsActive = false, Level = 0.6 };
        bars.SetResourceReference(EqualizerBars.TintProperty, "Notch.Text");
        return bars;
    }

    private static FrameworkElement Label(string text) => Text(text, "Notch.Numeric", verticalCenter: true);

    private static TextBlock Text(string text, string style, HorizontalAlignment align = HorizontalAlignment.Left, bool verticalCenter = false)
    {
        var block = new TextBlock { Text = text, HorizontalAlignment = align };
        block.SetResourceReference(FrameworkElement.StyleProperty, style);
        if (verticalCenter)
            block.VerticalAlignment = VerticalAlignment.Center;
        return block;
    }

    /// <summary>Equal columns with an 8 DIP gap.</summary>
    private sealed class UniformGrid2
    {
        private readonly int columns;

        public UniformGrid2(int columns)
        {
            this.columns = columns;
            for (var i = 0; i < columns; i++)
            {
                Root.ColumnDefinitions.Add(new ColumnDefinition());
                if (i < columns - 1)
                    Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            }
        }

        public Grid Root { get; } = new();

        public void Add(FrameworkElement child)
        {
            var index = Root.Children.Count;
            Grid.SetColumn(child, index % columns * 2);
            Grid.SetRow(child, index / columns);
            if (index / columns >= Root.RowDefinitions.Count)
                Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Root.Children.Add(child);
        }
    }
}

public sealed class DemoModule(string id, IReadOnlyList<NotchSection> sections, IReadOnlyList<CompactActivity> activities) : INotchModule
{
    public ModuleManifest Manifest { get; } = new() { Id = id, Name = id, Summary = id, Glyph = Glyphs.Info, Category = ModuleCategory.Productivity };
    public IReadOnlyList<NotchSection> Sections => sections;
    public IReadOnlyList<CompactActivity> Activities => activities;
    public IModuleContext? Context { get; private set; }

    public void Start(IModuleContext context) => Context = context;

    public void Stop() { }

    public FrameworkElement? CreateSettingsView() => null;

    public void DeleteData() { }
}

public sealed class DemoActivity(string id, int priority, string target, Func<FrameworkElement> leading, Func<FrameworkElement>? trailing = null)
    : CompactActivity(id, priority, target)
{
    public override FrameworkElement CreateLeading() => leading();

    public override FrameworkElement? CreateTrailing() => trailing?.Invoke();
}

/// <summary>Counts lifecycle calls.</summary>
public sealed class LifecycleView : Border, ISectionLifecycle
{
    public LifecycleView(UIElement child) => Child = child;

    public static int TotalShown { get; set; }
    public static int TotalHidden { get; set; }
    public int Shown { get; private set; }
    public int Hidden { get; private set; }

    public void OnShown()
    {
        Shown++;
        TotalShown++;
    }

    public void OnHidden()
    {
        Hidden++;
        TotalHidden++;
    }
}
