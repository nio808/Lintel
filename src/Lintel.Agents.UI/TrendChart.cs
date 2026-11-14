// SPDX-License-Identifier: GPL-3.0-or-later
// Portions adapted from vorssaint-utils, Copyright (C) 2026 Vorssaint, GPL-3.0-or-later.
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Lintel.Agents.UI;

/// <summary>One bar: a value per agent (same order as the chart's brushes), its axis label and its hover read-out.</summary>
internal sealed record TrendBar(double[] Parts, string Label, string Detail)
{
    public double Total => Parts.Sum();

    public bool SameAs(TrendBar other) => Label == other.Label && Detail == other.Detail && Parts.AsSpan().SequenceEqual(other.Parts);
}

/// <summary>Stacked bars drawn in OnRender (no element per bar). Hover dims the others and reports the index.</summary>
internal sealed class TrendChart : FrameworkElement
{
    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(
        nameof(Bars), typeof(IReadOnlyList<TrendBar>), typeof(TrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PartBrushesProperty = DependencyProperty.Register(
        nameof(PartBrushes), typeof(IReadOnlyList<Brush>), typeof(TrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoveredIndexProperty = DependencyProperty.Register(
        nameof(HoveredIndex), typeof(int), typeof(TrendChart),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private static readonly Brush Empty = AgentDisplay.Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    private static readonly Pen Guide = FrozenPen();

    public IReadOnlyList<TrendBar>? Bars
    {
        get => (IReadOnlyList<TrendBar>?)GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    public IReadOnlyList<Brush>? PartBrushes
    {
        get => (IReadOnlyList<Brush>?)GetValue(PartBrushesProperty);
        set => SetValue(PartBrushesProperty, value);
    }

    public int HoveredIndex
    {
        get => (int)GetValue(HoveredIndexProperty);
        set => SetValue(HoveredIndexProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));   // hit-testable everywhere
        var bars = Bars;
        var brushes = PartBrushes;
        if (bars is null || bars.Count == 0 || brushes is null || width <= 0 || height <= 0)
            return;
        var (step, barWidth) = Layout(bars.Count, width);
        var peak = bars.Max(b => b.Total);
        // Half-way guide, so heights read against something.
        dc.DrawLine(Guide, new Point(0, Math.Round(height / 2) + 0.5), new Point(width, Math.Round(height / 2) + 0.5));
        var hovered = HoveredIndex;
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var x = i * step;
            var total = bar.Total;
            if (total <= 0 || peak <= 0)
            {
                dc.DrawRoundedRectangle(Empty, null, new Rect(x, height - 2, barWidth, 2), 1, 1);
                continue;
            }
            var barHeight = Math.Max(2, height * total / peak);
            var radius = Math.Min(3, barWidth / 2);
            dc.PushClip(new RectangleGeometry(new Rect(x, height - barHeight, barWidth, barHeight), radius, radius));
            dc.PushOpacity(hovered < 0 ? 0.92 : hovered == i ? 1 : 0.35);
            var y = height;
            for (var p = 0; p < bar.Parts.Length && p < brushes.Count; p++)
            {
                var part = barHeight * bar.Parts[p] / total;
                if (part <= 0)
                    continue;
                dc.DrawRectangle(brushes[p], null, new Rect(x, y - part, barWidth, part + 0.5));
                y -= part;
            }
            dc.Pop();
            dc.Pop();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Bars is not { Count: > 0 } bars)
            return;
        var (step, _) = Layout(bars.Count, ActualWidth);
        HoveredIndex = Math.Clamp((int)(e.GetPosition(this).X / step), 0, bars.Count - 1);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        HoveredIndex = -1;
    }

    private static (double Step, double BarWidth) Layout(int count, double width)
    {
        var gap = count > 24 ? 3 : count > 12 ? 4 : 10;
        var barWidth = Math.Max(1, (width - gap * (count - 1)) / count);
        return (barWidth + gap, barWidth);
    }

    private static Pen FrozenPen()
    {
        var pen = new Pen(AgentDisplay.Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), 1) { DashStyle = new DashStyle([2, 3], 0) };
        pen.Freeze();
        return pen;
    }
}
