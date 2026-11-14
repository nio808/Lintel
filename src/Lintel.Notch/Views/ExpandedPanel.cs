// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Lintel.Core.Controls;
using Lintel.Core.Notch;
using Lintel.Core.Services;
using Lintel.Notch.Layout;
using Lintel.Notch.Motion;

namespace Lintel.Notch.Views;

/// <summary>
/// The open panel: a 30 DIP header (24 DIP glyph-only section tabs left, pin + settings right) above the
/// section host. Laid out once at its final size; the island's clip reveals it.
/// </summary>
internal sealed class ExpandedPanel : Grid
{
    private const double TabSize = 24, TabGap = 2, GlyphSize = 12;
    private static readonly CornerRadius TabRadius = new(6);

    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton pin = HeaderButton(new ToggleButton(), Glyphs.Pin, "Pin open");
    private readonly Button gear = HeaderButton(new Button(), Glyphs.Settings, "Settings");
    private readonly Border empty;
    private readonly Dictionary<string, Tab> tabCache = [];
    private IReadOnlyList<NotchSection> sections = [];

    public ExpandedPanel(Animator animator, IAppLog log)
    {
        Host = new SectionHost(animator, log);
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(NotchMetrics.HeaderHeight) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Background = Brushes.Transparent;   // clicks on empty panel space stay in the panel

        pin.Click += (_, _) => PinClicked?.Invoke();
        gear.Click += (_, _) => SettingsClicked?.Invoke();
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Children = { pin, gear } };
        pin.Margin = new Thickness(0, 0, TabGap, 0);

        var header = new Grid { Margin = new Thickness(NotchMetrics.SidePadding - 3, 0, NotchMetrics.SidePadding - 3, 0), Children = { tabs, right } };
        Children.Add(header);

        var body = new Border { Padding = new Thickness(NotchMetrics.SidePadding, 0, NotchMetrics.SidePadding, NotchMetrics.BottomPadding), Child = Host };
        SetRow(body, 1);
        Children.Add(body);

        var emptyGlyph = new TextBlock { Text = Glyphs.Add, HorizontalAlignment = HorizontalAlignment.Center };
        emptyGlyph.SetResourceReference(StyleProperty, "Notch.Icon");
        emptyGlyph.FontSize = 14;
        emptyGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Notch.TextSecondary");
        var emptyHint = new TextBlock { Text = "Add features in Settings", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 8) };
        emptyHint.SetResourceReference(TextBlock.ForegroundProperty, "Notch.TextSecondary");
        var open = new Button { Content = "Open Settings", HorizontalAlignment = HorizontalAlignment.Center, Focusable = false };
        open.SetResourceReference(StyleProperty, "Notch.PillButton");
        open.FontSize = 10;
        open.Click += (_, _) => SettingsClicked?.Invoke();
        empty = new Border { Visibility = Visibility.Collapsed, Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { emptyGlyph, emptyHint, open } } };
        SetRow(empty, 1);
        Children.Add(empty);
    }

    public SectionHost Host { get; }

    public event Action<string>? TabClicked;

    public event Action? PinClicked;

    public event Action? SettingsClicked;

    public Size Size { get; private set; }

    public bool Pinned
    {
        set => pin.IsChecked = value;
    }

    public void SetSections(IReadOnlyList<NotchSection> list)
    {
        sections = list;
        BuildTabs();
    }

    /// <summary>Lay out for <paramref name="section"/> (null = empty state) and show it.</summary>
    public void Show(NotchSection? section, int direction, bool animate)
    {
        var content = section is null ? NotchMetrics.MinContent : NotchMetrics.ClampContent(section.PreferredSize);
        Size = NotchMetrics.PanelSize(content);
        Width = Size.Width;
        Height = Size.Height;
        Host.Show(section, content, direction, animate);
        empty.Visibility = section is null ? Visibility.Visible : Visibility.Collapsed;
        BuildTabs();
    }

    private void BuildTabs()
    {
        // Tabs are created once per section and only restyled afterwards (no template churn when opening).
        var wanted = sections.Select(s => s.Id).ToList();
        if (!tabs.Children.OfType<Button>().Select(b => ((Tab)b.Tag).Id).SequenceEqual(wanted))
        {
            tabs.Children.Clear();
            foreach (var section in sections)
            {
                if (!tabCache.TryGetValue(section.Id, out var tab))
                    tabCache[section.Id] = tab = new Tab(section.Id, id => TabClicked?.Invoke(id));
                tab.Set(section);
                tabs.Children.Add(tab.Button);
            }
            foreach (var stale in tabCache.Keys.Except(wanted).ToList())
                tabCache.Remove(stale);
        }
        foreach (var tab in tabCache.Values)
            tab.SetActive(tab.Id == Host.CurrentId);
    }

    private static T HeaderButton<T>(T button, string glyph, string name) where T : ButtonBase
    {
        button.SetResourceReference(StyleProperty, "Notch.IconButton");
        button.Width = button.Height = TabSize;
        button.FontSize = GlyphSize;
        button.Content = glyph;
        button.Focusable = false;
        button.ToolTip = name;
        button.SetResourceReference(Control.ForegroundProperty, "Notch.TextSecondary");
        button.Resources["Notch.ButtonRadius"] = TabRadius;
        AutomationProperties.SetName(button, name);
        return button;
    }

    /// <summary>A glyph-only tab; the active one sits on a raised square (title in the tooltip / automation name).</summary>
    private sealed class Tab
    {
        private bool? active;

        public Tab(string id, Action<string> clicked)
        {
            Id = id;
            Button = HeaderButton(new Button(), "", id);
            Button.Margin = new Thickness(0, 0, TabGap, 0);
            Button.Tag = this;
            Button.Click += (_, _) => clicked(Id);
        }

        public string Id { get; }
        public Button Button { get; }

        public void Set(NotchSection section)
        {
            Button.Content = section.Glyph;
            Button.ToolTip = section.Title;
            AutomationProperties.SetName(Button, section.Title);
        }

        public void SetActive(bool value)
        {
            if (active == value)
                return;
            active = value;
            if (value)
            {
                Button.SetResourceReference(Control.BackgroundProperty, "Notch.SurfaceHover");
                Button.SetResourceReference(Control.ForegroundProperty, "Notch.Text");
            }
            else
            {
                Button.Background = Brushes.Transparent;
                Button.SetResourceReference(Control.ForegroundProperty, "Notch.TextSecondary");
            }
        }
    }
}
