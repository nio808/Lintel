// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lintel.Core.Controls;

/// <summary>
/// Circular gauge, clockwise from 12 o'clock. Value 0–100; changes animate over 300 ms. Optional <see cref="Pace"/> (0–100) draws
/// a small tick across the ring. Give it an explicit size.
/// </summary>
public sealed class RingGauge : FrameworkElement
{
    // Declared first: static initializers run in order and TrackBrushProperty's default uses it.
    private static readonly Brush Track = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush TickBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge), new PropertyMetadata(0d, OnValueChanged));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(3d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PaceProperty = DependencyProperty.Register(
        nameof(Pace), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(RingGauge), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RingGauge), new FrameworkPropertyMetadata(Track, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        "DisplayValue", typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public double Pace
    {
        get => (double)GetValue(PaceProperty);
        set => SetValue(PaceProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var thickness = Thickness;
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= thickness)
            return;
        var radius = (size - thickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        if (TrackBrush is { } track)
            dc.DrawEllipse(null, Frozen(new Pen(track, thickness)), center, radius, radius);

        var fraction = Math.Clamp((double)GetValue(DisplayValueProperty) / 100, 0, 1);
        if (fraction > 0 && Stroke is { } stroke)
            DrawArc(dc, stroke, thickness, center, radius, fraction);
        if (!double.IsNaN(Pace))
        {
            var a = Math.Clamp(Pace / 100, 0, 1) * 2 * Math.PI;
            var (inner, outer) = (radius - thickness / 2 - 0.5, radius + thickness / 2 + 0.5);
            var tick = Frozen(new Pen(TickBrush, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
            dc.DrawLine(tick, new Point(center.X + inner * Math.Sin(a), center.Y - inner * Math.Cos(a)), new Point(center.X + outer * Math.Sin(a), center.Y - outer * Math.Cos(a)));
        }
    }

    private static void DrawArc(DrawingContext dc, Brush stroke, double thickness, Point center, double radius, double fraction)
    {
        var pen = Frozen(new Pen(stroke, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        if (fraction >= 0.9999)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }
        var angle = fraction * 2 * Math.PI;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(center.X, center.Y - radius), false, false);
            context.ArcTo(new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle)),
                new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true, false);
        }
        dc.DrawGeometry(null, pen, Frozen(geometry));
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (RingGauge)d;
        var target = Math.Clamp((double)e.NewValue, 0, 100);
        if (!gauge.IsLoaded || !SystemParameters.ClientAreaAnimation)
        {
            gauge.BeginAnimation(DisplayValueProperty, null);
            gauge.SetValue(DisplayValueProperty, target);
            return;
        }
        gauge.BeginAnimation(DisplayValueProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(300)) { EasingFunction = Ease });
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
