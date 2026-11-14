// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lintel.Core.Agents;

namespace Lintel.Agents.UI;

/// <summary>Our own small mark per agent (no maker artwork): a sparkle, a prompt, a face, brackets. Give it a square size.</summary>
internal sealed class ProviderMark : FrameworkElement
{
    public static readonly DependencyProperty ProviderProperty = DependencyProperty.Register(
        nameof(Provider), typeof(AgentProvider), typeof(ProviderMark), new FrameworkPropertyMetadata(AgentProvider.Claude, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(ProviderMark), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    // Unit box -1…1. Stroked marks use round caps at ~13% of the box.
    private static readonly Geometry Sparkle = Freeze(Geometry.Parse("M0,-1 C0.1,-0.22 0.22,-0.1 1,0 C0.22,0.1 0.1,0.22 0,1 C-0.1,0.22 -0.22,0.1 -1,0 C-0.22,-0.1 -0.1,-0.22 0,-1 Z"));
    private static readonly Geometry Prompt = Freeze(Geometry.Parse("M-0.72,-0.5 L-0.12,0 L-0.72,0.5 M0.05,0.62 L0.78,0.62"));
    private static readonly Geometry Face = Freeze(Geometry.Parse("M-0.6,-0.5 L0.6,-0.5 A0.32,0.32 0 0 1 0.92,-0.18 L0.92,0.5 A0.32,0.32 0 0 1 0.6,0.82 L-0.6,0.82 A0.32,0.32 0 0 1 -0.92,0.5 L-0.92,-0.18 A0.32,0.32 0 0 1 -0.6,-0.5 Z M0,-0.5 L0,-0.86"));
    private static readonly Geometry Brackets = Freeze(Geometry.Parse("M-0.25,-0.8 L-0.75,-0.8 L-0.75,0.8 L-0.25,0.8 M0.25,-0.8 L0.75,-0.8 L0.75,0.8 L0.25,0.8"));

    public AgentProvider Provider
    {
        get => (AgentProvider)GetValue(ProviderProperty);
        set => SetValue(ProviderProperty, value);
    }

    public Brush? Tint
    {
        get => (Brush?)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0 || Tint is not { } tint)
            return;
        var half = size / 2;
        dc.PushTransform(new MatrixTransform(half, 0, 0, half, ActualWidth / 2, ActualHeight / 2));
        if (Provider == AgentProvider.Claude)
        {
            dc.DrawGeometry(tint, null, Sparkle);
        }
        else
        {
            var pen = new Pen(tint, Math.Max(1.4, size * 0.13) / half) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, Provider switch { AgentProvider.Codex => Prompt, AgentProvider.Copilot => Face, _ => Brackets });
            if (Provider == AgentProvider.Copilot)
            {
                dc.DrawEllipse(tint, null, new Point(-0.36, 0.16), 0.15, 0.15);
                dc.DrawEllipse(tint, null, new Point(0.36, 0.16), 0.15, 0.15);
            }
        }
        dc.Pop();
    }

    private static Geometry Freeze(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }
}

internal sealed record SplitSegment(Brush Brush, double Weight);

/// <summary>One capsule split by agent; every agent with a share keeps a visible sliver.</summary>
internal sealed class SplitBar : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<SplitSegment>), typeof(SplitBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Track = AgentDisplay.Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));

    public IReadOnlyList<SplitSegment>? Segments
    {
        get => (IReadOnlyList<SplitSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 6);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;
        var radius = height / 2;
        var parts = Segments?.Where(s => s.Weight > 0).ToList() ?? [];
        var total = parts.Sum(s => s.Weight);
        if (total <= 0)
        {
            dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, width, height), radius, radius);
            return;
        }
        const double gap = 2;
        var room = Math.Max(0, width - gap * (parts.Count - 1) - height * parts.Count);
        var x = 0d;
        foreach (var part in parts)
        {
            var w = height + room * part.Weight / total;
            dc.DrawRoundedRectangle(part.Brush, null, new Rect(x, 0, w, height), radius, radius);
            x += w + gap;
        }
    }
}

/// <summary>A dot with a soft expanding halo while <see cref="IsPulsing"/>; 30 fps, only while visible, still without animations.</summary>
internal sealed class PulseDot : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(PulseDot), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsPulsingProperty = DependencyProperty.Register(
        nameof(IsPulsing), typeof(bool), typeof(PulseDot), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((PulseDot)d).Update()));

    public static readonly DependencyProperty DotSizeProperty = DependencyProperty.Register(
        nameof(DotSize), typeof(double), typeof(PulseDot), new FrameworkPropertyMetadata(6d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        "Phase", typeof(double), typeof(PulseDot), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private bool animating;

    public PulseDot()
    {
        IsVisibleChanged += (_, _) => Update();
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public bool IsPulsing
    {
        get => (bool)GetValue(IsPulsingProperty);
        set => SetValue(IsPulsingProperty, value);
    }

    public double DotSize
    {
        get => (double)GetValue(DotSizeProperty);
        set => SetValue(DotSizeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Fill is not { } fill)
            return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var core = DotSize / 2;
        if (animating)
        {
            var phase = (double)GetValue(PhaseProperty);
            var max = Math.Min(ActualWidth, ActualHeight) / 2;
            dc.PushOpacity(0.55 * (1 - phase) * (1 - phase));
            dc.DrawEllipse(fill, null, center, core + (max - core) * phase, core + (max - core) * phase);
            dc.Pop();
        }
        dc.DrawEllipse(fill, null, center, core, core);
    }

    private void Update()
    {
        var want = IsPulsing && IsVisible && SystemParameters.ClientAreaAnimation;
        if (want == animating)
            return;
        animating = want;
        if (!want)
        {
            BeginAnimation(PhaseProperty, null);
            InvalidateVisual();
            return;
        }
        var pulse = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(pulse, 30);
        BeginAnimation(PhaseProperty, pulse);
    }
}

/// <summary>A 270° arc turning once a second while visible.</summary>
internal sealed class Spinner : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Spinner), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty AngleProperty = DependencyProperty.Register(
        "Angle", typeof(double), typeof(Spinner), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private bool spinning;

    public Spinner()
    {
        IsVisibleChanged += (_, _) => Update();
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 3 || Stroke is not { } stroke)
            return;
        var thickness = Math.Max(1.5, size / 8);
        var radius = (size - thickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var start = (double)GetValue(AngleProperty) * Math.PI / 180;
        var end = start + 1.5 * Math.PI;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(center.X + radius * Math.Sin(start), center.Y - radius * Math.Cos(start)), false, false);
            g.ArcTo(new Point(center.X + radius * Math.Sin(end), center.Y - radius * Math.Cos(end)), new Size(radius, radius), 0, true, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        var pen = new Pen(stroke, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private void Update()
    {
        if (IsVisible == spinning)
            return;
        spinning = IsVisible;
        if (!spinning)
        {
            BeginAnimation(AngleProperty, null);
            return;
        }
        BeginAnimation(AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
    }
}
