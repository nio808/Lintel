// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Lintel.Core.Controls;
using Lintel.Core.Notch;

namespace Lintel.Tools;

/// <summary>A panel of the shared Tools section, contributed by KeepAwakeModule or TogglesModule while it runs.</summary>
internal interface IToolsPanel
{
    int Order { get; }
    /// <summary>Fixed width in DIP, or null to share the rest.</summary>
    double? Width { get; }
    /// <summary>Called once per section view; the element may implement ISectionLifecycle.</summary>
    FrameworkElement Create();
}

/// <summary>Registry behind the shared "tools" section: both modules return the same section; the view shows the panels of whichever are running.</summary>
internal static class ToolsSection
{
    private static readonly List<IToolsPanel> Panels = [];

    public static event Action? Changed;

    public static IReadOnlyList<IToolsPanel> Current
    {
        get { lock (Panels) return Panels.OrderBy(p => p.Order).ToList(); }
    }

    public static void Register(IToolsPanel panel)
    {
        lock (Panels) Panels.Add(panel);
        Changed?.Invoke();
    }

    public static void Unregister(IToolsPanel panel)
    {
        lock (Panels) Panels.Remove(panel);
        Changed?.Invoke();
    }

    public static NotchSection Create() => new()
    {
        Id = "tools", Title = "Tools", Glyph = Glyphs.Wrench, Order = 100,
        PreferredSize = new Size(460, 200),
        CreateView = () => new ToolsSectionView(),
    };
}

internal sealed class ToolsSectionView : Grid, ISectionLifecycle
{
    private readonly Dictionary<IToolsPanel, FrameworkElement> elements = [];
    private bool shown;

    // subscribes for the life of the process; the host creates this view once per section.
    public ToolsSectionView()
    {
        ToolsSection.Changed += () => Dispatcher.InvokeAsync(Rebuild);
        Rebuild();
    }

    public void OnShown()
    {
        shown = true;
        foreach (var element in elements.Values)
            (element as ISectionLifecycle)?.OnShown();
    }

    public void OnHidden()
    {
        shown = false;
        foreach (var element in elements.Values)
            (element as ISectionLifecycle)?.OnHidden();
    }

    private void Rebuild()
    {
        var panels = ToolsSection.Current;
        foreach (var gone in elements.Keys.Except(panels).ToList())
        {
            (elements[gone] as ISectionLifecycle)?.OnHidden();
            elements.Remove(gone);
        }
        Children.Clear();
        ColumnDefinitions.Clear();
        Margin = new Thickness(0, 2, 0, 0);
        for (var i = 0; i < panels.Count; i++)
        {
            var panel = panels[i];
            if (!elements.TryGetValue(panel, out var element))
            {
                element = panel.Create();
                elements[panel] = element;
                if (shown)
                    (element as ISectionLifecycle)?.OnShown();
            }
            ColumnDefinitions.Add(new ColumnDefinition { Width = panel.Width is { } w ? new GridLength(w) : new GridLength(1, GridUnitType.Star) });
            element.Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0);
            SetColumn(element, i);
            Children.Add(element);
        }
    }
}
