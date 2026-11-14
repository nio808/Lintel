// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Lintel.Tools.Ui;

/// <summary>Tiny element factories that reference the notch theme by resource key (the theme is merged into the host window, not App).</summary>
internal static class Kit
{
    public static TextBlock Text(string text = "", string style = "Notch.Body", string? foreground = null)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(FrameworkElement.StyleProperty, style);
        if (foreground is not null)
            block.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        return block;
    }

    public static TextBlock Icon(string glyph, double size = 12, string? foreground = null)
    {
        var block = new TextBlock { Text = glyph };
        if (size != 12)
            block.FontSize = size;
        block.SetResourceReference(FrameworkElement.StyleProperty, "Notch.Icon");
        if (foreground is not null)
            block.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        return block;
    }

    public static Button IconButton(string glyph, string tip, Action click)
    {
        var button = new Button { Content = glyph, ToolTip = tip };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Notch.IconButton");
        button.Click += (_, _) => click();
        return button;
    }

    public static Button PillButton(string text, Action click, bool accent = false)
    {
        var button = new Button { Content = text };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Notch.PillButton");
        if (accent)
        {
            button.SetResourceReference(Control.BackgroundProperty, "Notch.Accent");
            button.SetResourceReference(Control.ForegroundProperty, "Notch.Background");
        }
        button.Click += (_, _) => click();
        return button;
    }

    public static ToggleButton PillToggle(string text, Action click)
    {
        var button = new ToggleButton { Content = text };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Notch.PillButton");
        button.Click += (_, _) => click();
        return button;
    }

    public static Border Card(UIElement child, Thickness? padding = null)
    {
        var card = new Border { Child = child };
        card.SetResourceReference(FrameworkElement.StyleProperty, "Notch.Card");
        if (padding is { } p)
            card.Padding = p;
        return card;
    }
}

/// <summary>Up to two series (the second as a thin line). Values are pushed by the owner; draws in OnRender, nothing runs by itself.</summary>
internal sealed class Sparkline : FrameworkElement
{
    public const int Capacity = 60;

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondStrokeProperty = DependencyProperty.Register(
        nameof(SecondStroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly Queue<double> first = new(Capacity);
    private readonly Queue<double> second = new(Capacity);

    /// <summary>Fixed top of the scale (e.g. 100 for percent); 0 = scale to the largest value seen (at least MinScale).</summary>
    public double FixedMax { get; set; }
    public double MinScale { get; set; } = 1;

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush? SecondStroke
    {
        get => (Brush?)GetValue(SecondStrokeProperty);
        set => SetValue(SecondStrokeProperty, value);
    }

    public void Push(double a, double? b = null)
    {
        Enqueue(first, a);
        if (b is { } value)
            Enqueue(second, value);
        InvalidateVisual();
    }

    private static void Enqueue(Queue<double> queue, double value)
    {
        if (queue.Count == Capacity)
            queue.Dequeue();
        queue.Enqueue(value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 2 || h <= 2 || first.Count < 2)
            return;
        var max = FixedMax > 0 ? FixedMax : Math.Max(MinScale, Math.Max(first.Max(), second.Count > 0 ? second.Max() : 0));
        if (Stroke is { } stroke)
            Draw(dc, first, max, w, h, stroke, fill: true);
        if (second.Count > 1 && SecondStroke is { } stroke2)
            Draw(dc, second, max, w, h, stroke2, fill: false);
    }

    private static void Draw(DrawingContext dc, Queue<double> values, double max, double w, double h, Brush stroke, bool fill)
    {
        var step = w / (Capacity - 1);
        var offset = w - (values.Count - 1) * step;   // newest sample sits on the right edge
        var points = values.Select((v, i) => new Point(offset + i * step, h - 1 - Math.Clamp(v / max, 0, 1) * (h - 3))).ToArray();
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], fill, false);
            ctx.PolyLineTo(points.Skip(1).ToList(), true, true);
            if (fill)
            {
                ctx.LineTo(new Point(points[^1].X, h), false, false);
                ctx.LineTo(new Point(points[0].X, h), false, false);
            }
        }
        geometry.Freeze();
        Brush? area = null;
        if (fill)
        {
            area = stroke.Clone();
            area.Opacity = 0.16;
            area.Freeze();
        }
        var pen = new Pen(stroke, fill ? 1.5 : 1) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        if (fill)
        {
            var edge = new StreamGeometry();
            using (var ctx = edge.Open())
            {
                ctx.BeginFigure(points[0], false, false);
                ctx.PolyLineTo(points.Skip(1).ToList(), true, true);
            }
            edge.Freeze();
            dc.DrawGeometry(area, null, geometry);
            dc.DrawGeometry(null, pen, edge);
        }
        else
        {
            dc.DrawGeometry(null, pen, geometry);
        }
    }
}
